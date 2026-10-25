using Wishlistarr.Api.Backup;
using static Wishlistarr.Tests.Backup.BackupTestSupport;

namespace Wishlistarr.Tests.Backup;

public sealed class BackupRetentionTests
{
    [Fact]
    public async Task Retention_orders_by_the_name_and_ignores_modified_times()
    {
        string path = await CreateDataAsync();
        try
        {
            string backups = Path.Combine(path, "backups");
            string[] older =
            [
                "wishlistarr-20260101T000000000Z-000001.zip",
                "wishlistarr-20260102T000000000Z-000001.zip",
                "wishlistarr-20260103T000000000Z-000001.zip",
                "wishlistarr-20260103T000000000Z-000002.zip",
            ];
            DateTime future = DateTime.UtcNow.AddDays(30);
            for (int index = 0; index < older.Length; index++)
            {
                string file = Path.Combine(backups, older[index]);
                await File.WriteAllTextAsync(file, "old");
                File.SetLastWriteTimeUtc(file, future.AddDays(-index));
            }

            await using StandaloneBackups standalone = new(path, retention: 3);
            BackupItem created = await standalone.Service.CreateBackupAsync();

            Assert.Equal(
                [older[2], older[3], created.Name],
                Directory.GetFiles(backups).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task The_new_backup_is_kept_when_the_clock_goes_backwards()
    {
        string path = await CreateDataAsync();
        try
        {
            string backups = Path.Combine(path, "backups");
            string[] later =
            [
                "wishlistarr-20260101T000000000Z-000001.zip",
                "wishlistarr-20260102T000000000Z-000001.zip",
                "wishlistarr-20260103T000000000Z-000001.zip",
            ];
            foreach (string name in later)
            {
                await File.WriteAllTextAsync(Path.Combine(backups, name), "later");
            }

            await using StandaloneBackups standalone = new(
                path,
                retention: 2,
                time: new FixedTime(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            BackupItem created = await standalone.Service.CreateBackupAsync();

            Assert.StartsWith("wishlistarr-20200101T", created.Name, StringComparison.Ordinal);
            Assert.Equal(
                [created.Name, later[2]],
                Directory.GetFiles(backups).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task Retention_never_deletes_files_that_are_not_backups()
    {
        string path = await CreateDataAsync();
        try
        {
            string backups = Path.Combine(path, "backups");
            string[] foreign =
            [
                "notes.txt",
                "wishlistarr-latest.zip",
                "wishlistarr-20200101T000000000Z-000001.zip.bak",
                "wishlistarr-20200101T000000000Z-000001.zip.partial-copy",
                "wishlistarr-٢٠٢٠٠١٠١T000000000Z-000001.zip",
                "wishlistarr-20200101T000000000Z-0000001.zip",
            ];
            foreach (string name in foreign)
            {
                await File.WriteAllTextAsync(Path.Combine(backups, name), "keep");
            }

            await File.WriteAllTextAsync(Path.Combine(backups, "wishlistarr-20200101T000000000Z-000001.zip"), "old");

            await using StandaloneBackups standalone = new(path, retention: 1);
            BackupItem created = await standalone.Service.CreateBackupAsync();

            string[] left = [.. Directory.GetFiles(backups).Select(Path.GetFileName).Cast<string>()];
            Assert.Contains(created.Name, left);
            Assert.DoesNotContain("wishlistarr-20200101T000000000Z-000001.zip", left);
            Assert.All(foreign, name => Assert.Contains(name, left));
            Assert.Equal(foreign.Length + 1, left.Length);
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    private static async Task<string> CreateDataAsync()
    {
        string path = TempDirectory();
        Directory.CreateDirectory(Path.Combine(path, "backups"));
        Directory.CreateDirectory(Path.Combine(path, "keys"));
        await File.WriteAllBytesAsync(Path.Combine(path, "keys", "key-1.xml"), KeyXml());
        await CreatePlainDatabaseAsync(Path.Combine(path, BackupNames.DatabaseEntryName));
        return path;
    }
}
