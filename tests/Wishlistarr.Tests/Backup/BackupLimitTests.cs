using Wishlistarr.Api.Backup;
using static Wishlistarr.Tests.Backup.BackupTestSupport;

namespace Wishlistarr.Tests.Backup;

public sealed class BackupLimitTests
{
    private const string Earlier = "wishlistarr-20200101T000000000Z-000001.zip";

    [Fact]
    public async Task A_backup_with_more_files_than_restore_accepts_is_refused_and_earlier_backups_are_kept()
    {
        string path = await CreateDataAsync();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(path, "keys", "key-2.xml"), KeyXml());
            ListLogger<BackupService> logger = new();

            await using StandaloneBackups standalone = new(path, retention: 1, options: new BackupOptions { MaxEntries = 2 }, logger: logger);
            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => standalone.Service.CreateBackupAsync());

            Assert.Contains("Backups:MaxEntries", refused.Message, StringComparison.Ordinal);
            Assert.Contains(logger.Errors, error => error.Contains("Backups:MaxEntries", StringComparison.Ordinal));
            AssertOnlyEarlierBackupRemains(path);
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task A_database_over_the_restore_size_limit_is_refused_and_earlier_backups_are_kept()
    {
        string path = await CreateDataAsync();
        try
        {
            ListLogger<BackupService> logger = new();

            await using StandaloneBackups standalone = new(path, retention: 1, options: new BackupOptions { MaxRestoreBytes = 1024 }, logger: logger);
            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => standalone.Service.CreateBackupAsync());

            Assert.Contains("Backups:MaxRestoreBytes", refused.Message, StringComparison.Ordinal);
            Assert.Single(logger.Errors);
            AssertOnlyEarlierBackupRemains(path);
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task A_zip_over_the_upload_limit_is_refused_and_earlier_backups_are_kept()
    {
        string path = await CreateDataAsync();
        try
        {
            ListLogger<BackupService> logger = new();

            await using StandaloneBackups standalone = new(path, retention: 1, options: new BackupOptions { MaxUploadBytes = 64 }, logger: logger);
            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => standalone.Service.CreateBackupAsync());

            Assert.Contains("Backups:MaxUploadBytes", refused.Message, StringComparison.Ordinal);
            Assert.Single(logger.Errors);
            AssertOnlyEarlierBackupRemains(path);
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task A_backup_within_the_limits_is_written()
    {
        string path = await CreateDataAsync();
        try
        {
            await using StandaloneBackups standalone = new(path, options: new BackupOptions { MaxEntries = 2 });
            BackupItem created = await standalone.Service.CreateBackupAsync();

            Assert.True(File.Exists(Path.Combine(path, "backups", created.Name)));
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public void The_default_options_are_valid() => Assert.True(new BackupOptions().IsValid());

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(BackupOptions.HardMaxEntries, true)]
    [InlineData(BackupOptions.HardMaxEntries + 1, false)]
    public void The_entry_cap_is_validated(int entries, bool valid) =>
        Assert.Equal(valid, new BackupOptions { MaxEntries = entries }.IsValid());

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(BackupOptions.HardMaxRestoreBytes, true)]
    [InlineData(BackupOptions.HardMaxRestoreBytes + 1, false)]
    public void The_restore_size_cap_is_validated(long bytes, bool valid) =>
        Assert.Equal(valid, new BackupOptions { MaxRestoreBytes = bytes }.IsValid());

    private static void AssertOnlyEarlierBackupRemains(string path)
    {
        string[] files = [.. Directory.GetFiles(Path.Combine(path, "backups")).Select(file => Path.GetFileName(file))];
        Assert.Equal([Earlier], files);
    }

    private static async Task<string> CreateDataAsync()
    {
        string path = TempDirectory();
        Directory.CreateDirectory(Path.Combine(path, "backups"));
        Directory.CreateDirectory(Path.Combine(path, "keys"));
        await File.WriteAllTextAsync(Path.Combine(path, "backups", Earlier), "earlier");
        await File.WriteAllBytesAsync(Path.Combine(path, "keys", "key-1.xml"), KeyXml());
        await CreatePlainDatabaseAsync(Path.Combine(path, BackupNames.DatabaseEntryName));
        return path;
    }
}
