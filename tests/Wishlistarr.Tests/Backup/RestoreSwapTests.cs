using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wishlistarr.Api.Backup;
using Wishlistarr.Api.Settings;
using static Wishlistarr.Tests.Backup.BackupTestSupport;

namespace Wishlistarr.Tests.Backup;

public sealed class RestoreTemplate : IAsyncLifetime
{
    public const string SettingKey = "example.api_key";

    public const string OldSecret = "old-secret-before-restore";

    public const string NewSecret = "new-secret-from-backup";

    public string Root { get; } = TempDirectory();

    public string Staged => Path.Combine(this.Root, "target");

    public string StagedWithSidecars => Path.Combine(this.Root, "target-sidecars");

    public async Task InitializeAsync()
    {
        string source = Path.Combine(this.Root, "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(this.Staged);
        await WriteSecretAsync(source, NewSecret);
        await WriteSecretAsync(this.Staged, OldSecret);

        await using (StandaloneBackups backups = new(source))
        {
            await backups.Service.CreateBackupAsync();
        }

        byte[] zip = await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(source, "backups"), "wishlistarr-*.zip").Single());
        await using StandaloneBackups target = new(this.Staged);
        await using MemoryStream upload = new(zip);
        await target.Service.StageRestoreAsync(upload, zip.Length);
        CopyDirectory(this.Staged, this.StagedWithSidecars);
        await AddWalSidecarsAsync(this.StagedWithSidecars);
    }

    public Task DisposeAsync()
    {
        DeleteDirectory(this.Root);
        return Task.CompletedTask;
    }

    private static async Task WriteSecretAsync(string path, string secret)
    {
        await using (OwnedDataApiFactory factory = new(path))
        {
            HttpClient client = await factory.CreateAuthorizedClientAsync();
            HttpResponseMessage put = await client.PutAsJsonAsync(
                $"/api/v1/settings/{SettingKey}",
                new { value = secret, isSecret = true });
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        }

        SqliteConnection.ClearAllPools();
    }
}

public sealed partial class RestoreSwapTests(RestoreTemplate template) : IClassFixture<RestoreTemplate>, IDisposable
{
    private readonly List<string> cases = [];

    public static TheoryData<string> Steps =>
    [
        nameof(RestoreStep.JournalWritten),
        nameof(RestoreStep.DatabaseRetired),
        nameof(RestoreStep.KeysRetired),
        nameof(RestoreStep.DatabasePlaced),
        nameof(RestoreStep.KeysPlaced),
        nameof(RestoreStep.Committed),
    ];

    public void Dispose()
    {
        foreach (string path in this.cases)
        {
            DeleteDirectory(path);
        }
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public async Task A_failure_after_any_step_leaves_a_matching_database_and_key_ring(string stepName)
    {
        RestoreStep failAt = Enum.Parse<RestoreStep>(stepName);
        string path = this.NewCase();

        await using (StandaloneBackups backups = new(path))
        {
            backups.Service.AfterRestoreStep = step =>
            {
                if (step == failAt)
                {
                    throw new IOException("Injected failure.");
                }
            };
            await Assert.ThrowsAnyAsync<Exception>(() => backups.Service.ApplyPendingRestoreAsync());
        }

        if (failAt == RestoreStep.Committed)
        {
            Assert.Equal(RestoreTemplate.NewSecret, await DecryptedSettingAsync(path, RestoreTemplate.SettingKey));
            await using StandaloneBackups again = new(path);
            await again.Service.ApplyPendingRestoreAsync();
            Assert.Equal(RestoreTemplate.NewSecret, await DecryptedSettingAsync(path, RestoreTemplate.SettingKey));
            AssertNoRestoreState(path);
            return;
        }

        Assert.Equal(RestoreTemplate.OldSecret, await DecryptedSettingAsync(path, RestoreTemplate.SettingKey));
        AssertNoRestoreState(path);
        await using OwnedDataApiFactory restarted = new(path);
        Assert.Equal(RestoreTemplate.OldSecret, await ReadSettingAsync(restarted, RestoreTemplate.SettingKey));
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public async Task A_crash_after_any_step_is_recovered_at_startup(string stepName)
    {
        RestoreStep crashAt = Enum.Parse<RestoreStep>(stepName);
        string path = this.NewCase();
        string crashed = this.Track(TempDirectory());

        await using (StandaloneBackups backups = new(path))
        {
            backups.Service.AfterRestoreStep = step =>
            {
                if (step == crashAt)
                {
                    CopyDirectory(path, crashed);
                    throw new OperationCanceledException("Simulated crash.");
                }
            };
            await Assert.ThrowsAnyAsync<Exception>(() => backups.Service.ApplyPendingRestoreAsync());
        }

        string expected = crashAt == RestoreStep.Committed ? RestoreTemplate.NewSecret : RestoreTemplate.OldSecret;
        await using (StandaloneBackups recovering = new(crashed))
        {
            RestoreSwap swap = new(recovering.Directory, NullLogger.Instance, null);
            swap.Recover();
            Assert.False(swap.InProgress);
            Assert.False(Directory.Exists(recovering.Directory.RestorePreviousPath));
            Assert.Equal(expected, await DecryptedSettingAsync(crashed, RestoreTemplate.SettingKey));

            swap.Recover();
            Assert.Equal(expected, await DecryptedSettingAsync(crashed, RestoreTemplate.SettingKey));

            await recovering.Service.ApplyPendingRestoreAsync();
            await recovering.Service.ApplyPendingRestoreAsync();
        }

        Assert.Equal(RestoreTemplate.NewSecret, await DecryptedSettingAsync(crashed, RestoreTemplate.SettingKey));
        AssertNoRestoreState(crashed);
    }

    [Fact]
    public async Task A_key_ring_move_that_fails_puts_back_the_old_database_and_keys()
    {
        string path = this.NewCase();
        string keys = Path.Combine(path, "keys");

        await using (StandaloneBackups backups = new(path))
        {
            backups.Service.AfterRestoreStep = step =>
            {
                if (step == RestoreStep.DatabasePlaced)
                {
                    Directory.CreateDirectory(keys);
                    File.WriteAllText(Path.Combine(keys, "blocking.xml"), "in the way");
                }
            };
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => backups.Service.ApplyPendingRestoreAsync());
            Assert.IsType<IOException>(failure.InnerException);
        }

        Assert.False(File.Exists(Path.Combine(keys, "blocking.xml")));
        Assert.Equal(RestoreTemplate.OldSecret, await DecryptedSettingAsync(path, RestoreTemplate.SettingKey));
        AssertNoRestoreState(path);
    }

    [Fact]
    public async Task A_staged_restore_that_fails_the_startup_checks_keeps_the_old_data_and_the_app_starts()
    {
        string path = this.NewCase();
        await File.WriteAllTextAsync(Path.Combine(path, "restore-pending", BackupNames.DatabaseEntryName), "not a database");

        await using (StandaloneBackups backups = new(path))
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => backups.Service.ApplyPendingRestoreAsync());
            Assert.Contains("failed validation", failure.InnerException?.Message, StringComparison.Ordinal);
        }

        Assert.Equal(RestoreTemplate.OldSecret, await DecryptedSettingAsync(path, RestoreTemplate.SettingKey));
        AssertNoRestoreState(path);
        await using OwnedDataApiFactory restarted = new(path);
        Assert.Equal(RestoreTemplate.OldSecret, await ReadSettingAsync(restarted, RestoreTemplate.SettingKey));
    }

    [Fact]
    public async Task Startup_removes_orphaned_staging_folders_and_incomplete_restores_only()
    {
        string path = this.NewCase();
        File.Delete(Path.Combine(path, "restore-pending", BackupNames.ReadyFileName));
        string orphan = Path.Combine(path, "restore-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(orphan, "extracted", "keys"));
        await File.WriteAllBytesAsync(Path.Combine(orphan, "extracted", "keys", "key-1.xml"), KeyXml());
        string unrelated = Path.Combine(path, "restore-staging-keep");
        string upper = Path.Combine(path, "restore-staging-" + Guid.NewGuid().ToString("N").ToUpperInvariant());
        Directory.CreateDirectory(unrelated);
        Directory.CreateDirectory(upper);

        await using (StandaloneBackups backups = new(path))
        {
            await backups.Service.ApplyPendingRestoreAsync();
        }

        Assert.False(Directory.Exists(orphan));
        Assert.False(Directory.Exists(Path.Combine(path, "restore-pending")));
        Assert.True(Directory.Exists(unrelated));
        Assert.True(Directory.Exists(upper));
        Assert.Equal(RestoreTemplate.OldSecret, await DecryptedSettingAsync(path, RestoreTemplate.SettingKey));
    }

    [Fact]
    public async Task The_pre_restore_backup_is_kept_by_retention()
    {
        string path = this.NewCase();
        string backups = Path.Combine(path, "backups");
        Directory.CreateDirectory(backups);
        await File.WriteAllTextAsync(Path.Combine(backups, "wishlistarr-20990101T000000000Z-000001.zip"), "later");

        await using (StandaloneBackups service = new(path, retention: 1))
        {
            await service.Service.ApplyPendingRestoreAsync();
        }

        string kept = Directory.GetFiles(backups).Single();
        Assert.NotEqual("wishlistarr-20990101T000000000Z-000001.zip", Path.GetFileName(kept));
        Assert.Equal(RestoreTemplate.NewSecret, await DecryptedSettingAsync(path, RestoreTemplate.SettingKey));
    }

    [Fact]
    public async Task Cleanup_after_a_committed_restore_keeps_a_restore_staged_since()
    {
        string path = this.NewCase();
        string crashed = this.Track(TempDirectory());
        await using (StandaloneBackups backups = new(path))
        {
            backups.Service.AfterRestoreStep = step =>
            {
                if (step == RestoreStep.Committed)
                {
                    CopyDirectory(path, crashed);
                    throw new OperationCanceledException("Simulated crash.");
                }
            };
            await Assert.ThrowsAnyAsync<Exception>(() => backups.Service.ApplyPendingRestoreAsync());
        }

        string pending = Path.Combine(crashed, "restore-pending");
        DeleteDirectory(pending);
        CopyDirectory(Path.Combine(template.Staged, "restore-pending"), pending);
        await File.WriteAllTextAsync(Path.Combine(pending, BackupNames.ReadyFileName), "a-newer-restore");

        await using (StandaloneBackups recovering = new(crashed))
        {
            RestoreSwap swap = new(recovering.Directory, NullLogger.Instance, null);
            swap.Recover();
            Assert.False(swap.InProgress);
            Assert.False(Directory.Exists(recovering.Directory.RestorePreviousPath));
            Assert.True(recovering.Service.RestorePending);
            Assert.True(File.Exists(Path.Combine(pending, BackupNames.DatabaseEntryName)));
            Assert.True(Directory.Exists(Path.Combine(pending, "keys")));
            Assert.Equal("a-newer-restore", await File.ReadAllTextAsync(Path.Combine(pending, BackupNames.ReadyFileName)));
        }
    }

    [Fact]
    public async Task Staging_a_restore_is_refused_while_a_restore_journal_exists()
    {
        string path = this.NewCase();
        await File.WriteAllTextAsync(Path.Combine(path, "restore-journal.json"), "{}");
        string pendingBefore = await File.ReadAllTextAsync(Path.Combine(path, "restore-pending", BackupNames.ReadyFileName));

        await using (StandaloneBackups backups = new(path))
        {
            await using MemoryStream upload = new(Zip((BackupNames.DatabaseEntryName, [1, 2, 3])));
            BackupRejectedException refused = await Assert.ThrowsAsync<BackupRejectedException>(
                () => backups.Service.StageRestoreAsync(upload, upload.Length));
            Assert.Equal(BackupRejection.RestoreInProgress, refused.Rejection);
            Assert.Equal(409, refused.StatusCode);
        }

        Assert.Equal(pendingBefore, await File.ReadAllTextAsync(Path.Combine(path, "restore-pending", BackupNames.ReadyFileName)));
        Assert.Empty(Directory.GetDirectories(path, "restore-staging-*"));
    }

    [Fact]
    public async Task The_restore_endpoint_answers_409_while_a_restore_journal_exists()
    {
        string path = this.NewCase();
        File.Delete(Path.Combine(path, "restore-pending", BackupNames.ReadyFileName));
        await using OwnedDataApiFactory factory = new(path);
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        await File.WriteAllTextAsync(Path.Combine(path, "restore-journal.json"), "{}");

        ByteArrayContent file = new([1, 2, 3]);
        using MultipartFormDataContent content = new() { { file, "file", "backup.zip" } };
        HttpResponseMessage response = await client.PostAsync("/api/v1/backups/restore", content);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("earlier restore", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        File.Delete(Path.Combine(path, "restore-journal.json"));
    }

    internal static void AssertNoRestoreState(string path)
    {
        Assert.False(File.Exists(Path.Combine(path, "restore-journal.json")));
        Assert.False(File.Exists(Path.Combine(path, "restore-journal.json.tmp")));
        Assert.False(Directory.Exists(Path.Combine(path, "restore-previous")));
        Assert.False(Directory.Exists(Path.Combine(path, "restore-pending")));
        Assert.Empty(Directory.GetDirectories(path, "restore-staging-*"));
    }

    private static async Task<string?> ReadSettingAsync(OwnedDataApiFactory factory, string key)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SettingsStore>().GetAsync(key);
    }

    private string NewCase(bool withSidecars = false)
    {
        string path = this.Track(TempDirectory());
        CopyDirectory(withSidecars ? template.StagedWithSidecars : template.Staged, path);
        return path;
    }

    private string Track(string path)
    {
        this.cases.Add(path);
        return path;
    }
}
