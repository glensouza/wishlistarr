using Microsoft.Extensions.Logging.Abstractions;
using Wishlistarr.Api.Backup;
using static Wishlistarr.Tests.Backup.BackupTestSupport;

namespace Wishlistarr.Tests.Backup;

public sealed partial class RestoreSwapTests
{
    public static TheoryData<string, int> FailurePoints =>
        new()
        {
        { nameof(RestoreStep.JournalWritten), 1 },
        { nameof(RestoreStep.SidecarRetired), 1 },
        { nameof(RestoreStep.SidecarRetired), 2 },
        { nameof(RestoreStep.DatabaseRetired), 1 },
        { nameof(RestoreStep.KeysRetired), 1 },
        { nameof(RestoreStep.DatabasePlaced), 1 },
        { nameof(RestoreStep.KeysPlaced), 1 },
        };

    public static TheoryData<string, int> RollbackCrashPoints =>
        new()
        {
        { nameof(RestoreStep.RollbackKeysRestored), 1 },
        { nameof(RestoreStep.RollbackDatabaseRestored), 1 },
        { nameof(RestoreStep.RollbackSidecarRestored), 1 },
        { nameof(RestoreStep.RollbackSidecarRestored), 2 },
        };

    public static TheoryData<string, bool> CrashedFolders =>
        new()
        {
        { nameof(RestoreStep.KeysRetired), false },
        { nameof(RestoreStep.KeysPlaced), false },
        { nameof(RestoreStep.Committed), false },
        { nameof(RestoreStep.SidecarRetired), true },
        { nameof(RestoreStep.DatabasePlaced), true },
        };

    [Theory]
    [MemberData(nameof(FailurePoints))]
    public async Task A_failed_swap_puts_the_old_database_back_with_its_wal_files(string stepName, int occurrence)
    {
        RestoreStep failAt = Enum.Parse<RestoreStep>(stepName);
        string path = this.NewCase(withSidecars: true);
        int seen = 0;

        await RunSwapAsync(path, step =>
        {
            if (step == failAt && ++seen == occurrence)
            {
                throw new IOException("Injected failure.");
            }
        });

        AssertOldDatabaseWithWalFiles(path);
        Assert.Equal(WalMarker, await ReadWalMarkerAsync(path));
        Assert.Equal(RestoreTemplate.OldSecret, await DecryptedSettingAsync(path, RestoreTemplate.SettingKey));
        AssertRolledBack(path);
    }

    [Fact]
    public async Task A_committed_swap_leaves_none_of_the_old_wal_files_beside_the_new_database()
    {
        string path = this.NewCase(withSidecars: true);

        await using (StandaloneBackups backups = new(path))
        {
            await backups.Service.ApplyPendingRestoreAsync();
        }

        foreach (string suffix in SidecarSuffixes)
        {
            Assert.False(File.Exists(Path.Combine(path, "wishlistarr.db" + suffix)));
        }

        Assert.Null(await ReadWalMarkerAsync(path));
        Assert.Equal(RestoreTemplate.NewSecret, await DecryptedSettingAsync(path, RestoreTemplate.SettingKey));
        AssertNoRestoreState(path);
    }

    [Fact]
    public async Task A_rollback_journal_file_travels_with_the_old_database()
    {
        string path = this.NewCase();
        string journal = Path.Combine(path, "wishlistarr.db-journal");
        byte[] content = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(journal, content);

        await RunSwapAsync(path, step =>
        {
            if (step == RestoreStep.KeysPlaced)
            {
                throw new IOException("Injected failure.");
            }
        });

        Assert.Equal(content, await File.ReadAllBytesAsync(journal));
        AssertRolledBack(path);
    }

    [Theory]
    [MemberData(nameof(FailurePoints))]
    public async Task A_crash_between_moves_is_rolled_back_with_the_wal_files_and_the_restore_is_applied_again(string stepName, int occurrence)
    {
        string crashed = await this.CrashAsync(this.NewCase(withSidecars: true), Enum.Parse<RestoreStep>(stepName), occurrence, failAt: null);

        await this.AssertRecoversAsync(crashed);
    }

    [Theory]
    [MemberData(nameof(RollbackCrashPoints))]
    public async Task A_crash_during_a_rollback_is_recovered_with_the_wal_files(string stepName, int occurrence)
    {
        string crashed = await this.CrashAsync(this.NewCase(withSidecars: true), Enum.Parse<RestoreStep>(stepName), occurrence, failAt: RestoreStep.KeysPlaced);

        await this.AssertRecoversAsync(crashed);
    }

    [Theory]
    [MemberData(nameof(CrashedFolders))]
    public async Task The_app_starts_from_a_crashed_data_folder_and_decrypts_the_restored_secret(string stepName, bool withSidecars)
    {
        string crashed = await this.CrashAsync(this.NewCase(withSidecars), Enum.Parse<RestoreStep>(stepName), 1, failAt: null);

        await using (OwnedDataApiFactory app = new(crashed))
        {
            Assert.Equal(RestoreTemplate.NewSecret, await ReadSettingAsync(app, RestoreTemplate.SettingKey));
        }

        AssertNoRestoreState(crashed);
    }

    // The swap is run directly because the backup taken before it opens the database, which
    // checkpoints and removes the -wal and -shm files before the swap would see them.
    private static async Task RunSwapAsync(string path, Action<RestoreStep> hook)
    {
        await using StandaloneBackups backups = new(path);
        RestoreSwap swap = new(backups.Directory, NullLogger.Instance, hook);
        Assert.ThrowsAny<Exception>(swap.Run);
    }

    private static void AssertRolledBack(string path)
    {
        Assert.False(File.Exists(Path.Combine(path, "restore-journal.json")));
        Assert.False(File.Exists(Path.Combine(path, "restore-journal.json.tmp")));
        Assert.False(Directory.Exists(Path.Combine(path, "restore-previous")));
        Assert.True(File.Exists(Path.Combine(path, "restore-pending", BackupNames.ReadyFileName)));
        Assert.True(File.Exists(Path.Combine(path, "restore-pending", BackupNames.DatabaseEntryName)));
        Assert.True(Directory.Exists(Path.Combine(path, "restore-pending", "keys")));
    }

    private static void AssertOldDatabaseWithWalFiles(string path)
    {
        Assert.True(File.Exists(Path.Combine(path, "wishlistarr.db")));
        Assert.True(File.Exists(Path.Combine(path, "wishlistarr.db-wal")));
        Assert.True(File.Exists(Path.Combine(path, "wishlistarr.db-shm")));
    }

    private async Task<string> CrashAsync(string path, RestoreStep crashAt, int occurrence, RestoreStep? failAt)
    {
        string crashed = this.Track(TempDirectory());
        int seen = 0;
        bool crashedHere = false;

        await RunSwapAsync(path, step =>
        {
            if (step == failAt)
            {
                throw new IOException("Injected failure.");
            }

            if (step == crashAt && ++seen == occurrence)
            {
                CopyDirectory(path, crashed);
                crashedHere = true;
                throw new OperationCanceledException("Simulated crash.");
            }
        });

        Assert.True(crashedHere);
        return crashed;
    }

    private async Task AssertRecoversAsync(string crashed)
    {
        await using StandaloneBackups recovering = new(crashed);
        RestoreSwap swap = new(recovering.Directory, NullLogger.Instance, null);
        swap.Recover();

        Assert.False(swap.InProgress);
        Assert.False(Directory.Exists(recovering.Directory.RestorePreviousPath));
        Assert.True(recovering.Service.RestorePending);
        AssertOldDatabaseWithWalFiles(crashed);
        Assert.Equal(WalMarker, await ReadWalMarkerAsync(crashed));
        Assert.Equal(RestoreTemplate.OldSecret, await DecryptedSettingAsync(crashed, RestoreTemplate.SettingKey));

        await recovering.Service.ApplyPendingRestoreAsync();

        Assert.Null(await ReadWalMarkerAsync(crashed));
        Assert.Equal(RestoreTemplate.NewSecret, await DecryptedSettingAsync(crashed, RestoreTemplate.SettingKey));
        AssertNoRestoreState(crashed);
    }
}
