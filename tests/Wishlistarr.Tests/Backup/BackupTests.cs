using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Backup;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Security;
using Wishlistarr.Api.Settings;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Backup;

public sealed class OwnedDataApiFactory : AuthApiFactory
{
    private readonly int retention;
    private readonly long? maxUploadBytes;
    private readonly bool scheduleBackups;

    public OwnedDataApiFactory(string dataPath, int retention = 7, long? maxUploadBytes = null, bool scheduleBackups = false)
        : base(dataPath, deleteData: false)
    {
        this.retention = retention;
        this.maxUploadBytes = maxUploadBytes;
        this.scheduleBackups = scheduleBackups;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Backups:Retention", this.retention.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (this.maxUploadBytes is long limit)
        {
            builder.UseSetting("Backups:MaxUploadBytes", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (this.scheduleBackups)
        {
            builder.UseSetting($"Jobs:Schedules:{DatabaseBackupJob.JobName}:Enabled", "true");
        }
    }
}

public sealed class BackupTests
{
    [Fact]
    public async Task Scheduled_and_manual_backups_keep_only_the_newest_retention_count()
    {
        string path = TempDirectory();
        try
        {
            await using OwnedDataApiFactory factory = new(path, retention: 2, scheduleBackups: true);
            HttpClient client = await factory.CreateAuthorizedClientAsync();
            JobRunner runner = factory.Services.GetRequiredService<JobRunner>();
            Assert.Equal(TimeSpan.FromDays(1), runner.Find(DatabaseBackupJob.JobName)!.DefaultInterval);

            JobRun? scheduled = null;
            for (int attempt = 0; attempt < 80 && scheduled is not { Outcome: JobRunOutcome.Succeeded }; attempt++)
            {
                await Task.Delay(50);
                scheduled = await runner.GetLastRunAsync(DatabaseBackupJob.JobName);
            }

            Assert.NotNull(scheduled);
            Assert.Equal(JobRunTrigger.Schedule, scheduled.Trigger);
            string first = OnlyZip(path);

            HttpResponseMessage manual = await client.PostAsync($"/api/v1/jobs/{DatabaseBackupJob.JobName}/run", null);
            Assert.Equal(HttpStatusCode.Accepted, manual.StatusCode);
            Assert.Equal(JobRunOutcome.Succeeded, (await WaitForIdleAsync(runner, scheduled.Id)).Outcome);
            string second = NewestZip(path);
            Assert.NotEqual(first, second);

            JobStartResult thirdRun = await runner.StartAsync(DatabaseBackupJob.JobName, JobRunTrigger.Schedule);
            await thirdRun.Completion;
            Assert.Equal(JobRunOutcome.Succeeded, (await runner.GetLastRunAsync(DatabaseBackupJob.JobName))!.Outcome);

            string[] left = ZipNames(path);
            Assert.Equal(2, left.Length);
            Assert.DoesNotContain(Path.GetFileName(first), left);
            Assert.Contains(Path.GetFileName(second), left);
            Assert.Contains(BackupNames.DatabaseEntryName, await EntryNamesAsync(NewestZip(path)));
            Assert.Contains(await EntryNamesAsync(NewestZip(path)), name => name.StartsWith("keys/", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task Pending_migration_is_backed_up_before_it_is_applied()
    {
        string path = TempDirectory();
        try
        {
            string latest = await CreateDowngradedDatabaseAsync(path);
            await using OwnedDataApiFactory restarted = new(path);
            await restarted.GetApiKeyAsync();

            string zip = OnlyZip(path);
            Assert.DoesNotContain(latest, await MigrationsInZipAsync(zip));
            Assert.False(await ZipHasColumnAsync(zip, "KidProfiles", "BlockedGenreIds"));
            Assert.Contains(latest, await MigrationsAsync(DatabasePath(path)));
            Assert.True(await HasColumnAsync(DatabasePath(path), "KidProfiles", "BlockedGenreIds"));
            Assert.Contains(await EntryNamesAsync(zip), name => name.StartsWith("keys/", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task Pending_migration_is_not_applied_when_the_backup_fails()
    {
        string path = TempDirectory();
        try
        {
            string latest = await CreateDowngradedDatabaseAsync(path);
            string backups = Path.Combine(path, "backups");
            await File.WriteAllTextAsync(backups, "not a directory");

            await using OwnedDataApiFactory factory = new(path);
            Exception? failure = await TryStartAsync(factory);

            Assert.NotNull(failure);
            Assert.Contains("pre-migration backup", Flatten(failure), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(latest, await MigrationsAsync(DatabasePath(path)));
            Assert.False(await HasColumnAsync(DatabasePath(path), "KidProfiles", "BlockedGenreIds"));
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task Fresh_and_current_databases_are_not_backed_up_on_startup()
    {
        string path = TempDirectory();
        try
        {
            await using (OwnedDataApiFactory first = new(path))
            {
                await first.GetApiKeyAsync();
                Assert.Empty(ZipNames(path));
            }

            SqliteConnection.ClearAllPools();
            await using (OwnedDataApiFactory second = new(path))
            {
                await second.GetApiKeyAsync();
            }

            Assert.Empty(ZipNames(path));
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task Restore_round_trip_decrypts_secrets_and_keeps_the_previous_data()
    {
        const string secret = "round-trip-secret-value";
        string source = TempDirectory();
        string restored = TempDirectory();
        try
        {
            byte[] zip;
            string sourceKey;
            string keyMaterial;
            await using (OwnedDataApiFactory sourceFactory = new(source))
            {
                HttpClient client = await sourceFactory.CreateAuthorizedClientAsync();
                HttpResponseMessage put = await client.PutAsJsonAsync(
                    "/api/v1/settings/example.api_key",
                    new { value = secret, isSecret = true });
                Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
                sourceKey = await sourceFactory.GetApiKeyAsync();
                await RunBackupAsync(sourceFactory);

                string backup = OnlyZip(source);
                zip = await File.ReadAllBytesAsync(backup);
                await using Stream zipStream = new MemoryStream(zip);
                using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);
                ZipArchiveEntry database = archive.GetEntry(BackupNames.DatabaseEntryName)!;
                ZipArchiveEntry key = archive.Entries.Single(entry => entry.FullName.StartsWith("keys/", StringComparison.Ordinal));
                using StreamReader keyReader = new(key.Open());
                string keyText = await keyReader.ReadToEndAsync();
                keyMaterial = keyText.Substring(keyText.Length / 2, 32);
                await using Stream databaseStream = database.Open();
                using MemoryStream databaseBytes = new();
                await databaseStream.CopyToAsync(databaseBytes);
                Assert.DoesNotContain(secret, Encoding.UTF8.GetString(databaseBytes.ToArray()));
                Assert.Contains(keyMaterial, keyText);

                string log = await ReadLogsAsync(source);
                Assert.DoesNotContain(secret, log);
                Assert.DoesNotContain(keyMaterial, log);

                HttpResponseMessage download = await client.GetAsync($"/api/v1/backups/{Path.GetFileName(backup)}/download");
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
                Assert.Equal("no-store", download.Headers.CacheControl?.ToString());
                byte[] downloaded = await download.Content.ReadAsByteArrayAsync();
                Assert.Equal(zip, downloaded);

                HttpClient kid = sourceFactory.CreateClient();
                kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");
                HttpResponseMessage asKid = await kid.GetAsync("/api/v1/backups");
                Assert.Equal(HttpStatusCode.Forbidden, asKid.StatusCode);
                Assert.DoesNotContain(secret, await asKid.Content.ReadAsStringAsync());
                Assert.DoesNotContain(keyMaterial, await asKid.Content.ReadAsStringAsync());
                HttpResponseMessage kidDownload = await kid.GetAsync($"/api/v1/backups/{Path.GetFileName(backup)}/download");
                Assert.Equal(HttpStatusCode.Forbidden, kidDownload.StatusCode);
                Assert.DoesNotContain(secret, await kidDownload.Content.ReadAsStringAsync());
            }

            SqliteConnection.ClearAllPools();
            string replacedKey = string.Empty;
            string replacedCipher = string.Empty;
            await using (OwnedDataApiFactory target = new(restored))
            {
                HttpClient client = await target.CreateAuthorizedClientAsync();
                replacedKey = await target.GetApiKeyAsync();
                Assert.NotEqual(sourceKey, replacedKey);

                using MultipartFormDataContent content = new();
                ByteArrayContent file = new(zip);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                content.Add(file, "file", "backup.zip");
                HttpResponseMessage response = await client.PostAsync("/api/v1/backups/restore", content);
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

                Assert.Equal(replacedKey, await target.GetApiKeyAsync());
                Assert.Null(await ReadSettingAsync(target, "example.api_key"));
                replacedCipher = await ReadRawSettingAsync(target, ApiKeyService.SettingKey);
                Assert.True(File.Exists(Path.Combine(restored, "restore-pending", BackupNames.ReadyFileName)));
                Assert.True(File.Exists(DatabasePath(restored)));
            }

            SqliteConnection.ClearAllPools();
            await using OwnedDataApiFactory restarted = new(restored);
            Assert.Equal(secret, await ReadSettingAsync(restarted, "example.api_key"));
            Assert.Equal(sourceKey, await ReadSettingAsync(restarted, ApiKeyService.SettingKey));
            Assert.False(Directory.Exists(Path.Combine(restored, "restore-pending")));
            string previous = OnlyZip(restored);
            Assert.Equal(replacedCipher, await SettingValueInZipAsync(previous, ApiKeyService.SettingKey));
            string restartedLog = await ReadLogsAsync(restored);
            Assert.DoesNotContain(secret, restartedLog);
            Assert.DoesNotContain(keyMaterial, restartedLog);
        }
        finally
        {
            DeleteDirectory(source);
            DeleteDirectory(restored);
        }
    }

    [Fact]
    public async Task Restore_requires_antiforgery_for_a_browser_session_and_rejects_bad_archives()
    {
        string path = TempDirectory();
        try
        {
            await using OwnedDataApiFactory factory = new(path, maxUploadBytes: 1024 * 1024);
            await factory.EnsureParentAsync();
            HttpClient browser = factory.CreateClient();
            HttpResponseMessage login = await browser.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword });
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

            using MultipartFormDataContent missingToken = Upload(Zip(("readme.txt", "nope"u8.ToArray())));
            HttpResponseMessage forbidden = await browser.PostAsync("/api/v1/backups/restore", missingToken);
            Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);
            Assert.Contains("antiforgery", await forbidden.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(path, "restore-pending", BackupNames.ReadyFileName)));

            await AuthApiFactory.AddAntiforgeryTokenAsync(browser);
            string marker = "wishlistarr-slip-marker.txt";
            await AssertRejectedAsync(browser, Zip(("../" + marker, "nope"u8.ToArray()), ("keys/../../" + marker, "nope"u8.ToArray()), ("keys\\..\\" + marker, "nope"u8.ToArray())), "unsafe path");
            Assert.False(File.Exists(Path.Combine(path, marker)));
            Assert.False(File.Exists(Path.Combine(Directory.GetParent(path)!.FullName, marker)));

            await AssertRejectedAsync(browser, Encoding.UTF8.GetBytes("this is not a zip"), "not a valid backup zip");
            await AssertRejectedAsync(browser, Zip((BackupNames.DatabaseEntryName, "not a database"u8.ToArray()), ("keys/key.xml", KeyXml())), "integrity check");
            await AssertRejectedAsync(browser, await FutureSchemaZipAsync(), "newer version");
            await AssertRejectedAsync(browser, Zip((BackupNames.DatabaseEntryName, await EmptyDatabaseAsync()), ("keys/key.xml", KeyXml()), ("readme.txt", "notes"u8.ToArray())), "not part of a Wishlistarr backup");
            Assert.False(Directory.Exists(Path.Combine(path, "restore-pending")));

            await using OwnedDataApiFactory tiny = new(TempChild(path, "tiny"), maxUploadBytes: 32);
            HttpClient limited = await tiny.CreateAuthorizedClientAsync();
            await AssertRejectedAsync(limited, new byte[64], "larger than the allowed size");
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task Download_rejects_names_that_are_not_backup_files()
    {
        string path = TempDirectory();
        try
        {
            await using OwnedDataApiFactory factory = new(path);
            HttpClient client = await factory.CreateAuthorizedClientAsync();

            HttpResponseMessage invalid = await client.GetAsync("/api/v1/backups/not-a-backup/download");
            HttpResponseMessage missing = await client.GetAsync("/api/v1/backups/wishlistarr-20260101T000000000Z-000001.zip/download");

            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    private static async Task AssertRejectedAsync(HttpClient client, byte[] body, string title)
    {
        using MultipartFormDataContent content = Upload(body);
        HttpResponseMessage response = await client.PostAsync("/api/v1/backups/restore", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(title, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private static MultipartFormDataContent Upload(byte[] body)
    {
        ByteArrayContent file = new(body);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return new MultipartFormDataContent { { file, "file", "backup.zip" } };
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using Stream output = entry.Open();
                output.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static async Task<byte[]> FutureSchemaZipAsync()
    {
        string directory = TempDirectory();
        try
        {
            string database = Path.Combine(directory, BackupNames.DatabaseEntryName);
            await using (SqliteConnection connection = new($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE "__EFMigrationsHistory" (
                        "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                        "ProductVersion" TEXT NOT NULL
                    );
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('99999999999999_FromTheFuture', '10.0.0');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            return Zip((BackupNames.DatabaseEntryName, await File.ReadAllBytesAsync(database)), ("keys/key-future.xml", KeyXml()));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static async Task<byte[]> EmptyDatabaseAsync()
    {
        string directory = TempDirectory();
        try
        {
            string database = Path.Combine(directory, BackupNames.DatabaseEntryName);
            await using (SqliteConnection connection = new($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE example (id INTEGER PRIMARY KEY);";
                await command.ExecuteNonQueryAsync();
            }

            return await File.ReadAllBytesAsync(database);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static byte[] KeyXml() =>
        """
        <?xml version="1.0" encoding="utf-8"?>
        <key id="11111111-1111-1111-1111-111111111111" version="1" />
        """u8.ToArray();

    private static async Task<string> CreateDowngradedDatabaseAsync(string path)
    {
        await using (OwnedDataApiFactory factory = new(path))
        {
            HttpClient client = await factory.CreateAuthorizedClientAsync();
            await client.PutAsJsonAsync("/api/v1/settings/example.api_key", new { value = "downgrade-secret", isSecret = true });
        }

        SqliteConnection.ClearAllPools();
        ServiceCollection services = new();
        services.AddDbContext<WishlistarrDbContext>(options => options.UseWishlistarrSqlite(DatabasePath(path)));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        string[] migrations = [.. db.Database.GetMigrations()];
        string latest = migrations[^1];
        IMigrator migrator = db.Database.GetService<IMigrator>() ?? throw new InvalidOperationException("No migrator.");
        await migrator.MigrateAsync(migrations[^2]);
        await db.Database.CloseConnectionAsync();
        SqliteConnection.ClearAllPools();
        return latest;
    }

    private static async Task RunBackupAsync(OwnedDataApiFactory factory)
    {
        JobRunner runner = factory.Services.GetRequiredService<JobRunner>();
        JobStartResult result = await runner.StartAsync(DatabaseBackupJob.JobName, JobRunTrigger.Manual);
        Assert.Equal(JobStartStatus.Started, result.Status);
        await result.Completion;
        Assert.Equal(JobRunOutcome.Succeeded, (await runner.GetLastRunAsync(DatabaseBackupJob.JobName))!.Outcome);
    }

    private static async Task<JobRun> WaitForIdleAsync(JobRunner runner, int previousId)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            JobRun? last = await runner.GetLastRunAsync(DatabaseBackupJob.JobName);
            if (!runner.IsRunning(DatabaseBackupJob.JobName) && last is { Id: > 0 } && last.Id != previousId && last.Outcome != JobRunOutcome.Running)
            {
                return last;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The backup job did not finish.");
    }

    private static async Task<string?> ReadSettingAsync(OwnedDataApiFactory factory, string key)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SettingsStore>().GetAsync(key);
    }

    private static async Task<string> ReadRawSettingAsync(OwnedDataApiFactory factory, string key)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        Setting setting = await db.Settings.AsNoTracking().SingleAsync(row => row.Key == key);
        return setting.Value;
    }

    private static async Task<Exception?> TryStartAsync(OwnedDataApiFactory factory)
    {
        try
        {
            using HttpClient client = factory.CreateClient();
            await client.GetAsync("/api/health");
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<IReadOnlyList<string>> MigrationsAsync(string databasePath)
    {
        IReadOnlyList<string>? ids = await SqliteSnapshot.ReadMigrationsAsync(databasePath, default);
        return ids ?? [];
    }

    private static async Task<IReadOnlyList<string>> MigrationsInZipAsync(string zipPath)
    {
        string database = await ExtractZipDatabaseAsync(zipPath);
        try
        {
            return await MigrationsAsync(database);
        }
        finally
        {
            DeleteDirectory(Path.GetDirectoryName(database)!);
        }
    }

    private static async Task<bool> ZipHasColumnAsync(string zipPath, string table, string column)
    {
        string database = await ExtractZipDatabaseAsync(zipPath);
        try
        {
            return await HasColumnAsync(database, table, column);
        }
        finally
        {
            DeleteDirectory(Path.GetDirectoryName(database)!);
        }
    }

    private static async Task<string> SettingValueInZipAsync(string zipPath, string key)
    {
        string database = await ExtractZipDatabaseAsync(zipPath);
        try
        {
            await using SqliteConnection connection = new($"Data Source={database};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT \"Value\" FROM \"Settings\" WHERE \"Key\" = @key;";
            command.Parameters.AddWithValue("@key", key);
            return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
        }
        finally
        {
            DeleteDirectory(Path.GetDirectoryName(database)!);
        }
    }

    private static async Task<string> ExtractZipDatabaseAsync(string zipPath)
    {
        string directory = TempDirectory();
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry entry = archive.GetEntry(BackupNames.DatabaseEntryName) ?? throw new InvalidOperationException("Backup has no database.");
        string database = Path.Combine(directory, BackupNames.DatabaseEntryName);
        entry.ExtractToFile(database);
        await Task.CompletedTask;
        return database;
    }

    private static async Task<IReadOnlyList<string>> EntryNamesAsync(string zipPath)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        await Task.CompletedTask;
        return [.. archive.Entries.Select(entry => entry.FullName)];
    }

    private static async Task<bool> HasColumnAsync(string databasePath, string table, string column)
    {
        await using SqliteConnection connection = new($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @column;";
        command.Parameters.AddWithValue("@column", column);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<string> ReadLogsAsync(string path)
    {
        string logs = Path.Combine(path, "logs");
        string content = string.Empty;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Directory.Exists(logs))
            {
                foreach (string file in Directory.GetFiles(logs, "wishlistarr-*.log"))
                {
                    await using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using StreamReader reader = new(stream);
                    content += await reader.ReadToEndAsync();
                }
            }

            if (content.Contains("Database backup", StringComparison.Ordinal))
            {
                return content;
            }

            await Task.Delay(50);
        }

        return content;
    }

    private static string Flatten(Exception exception)
    {
        StringBuilder builder = new();
        void Walk(Exception current)
        {
            builder.AppendLine(current.Message);
            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    Walk(inner);
                }

                return;
            }

            if (current.InnerException is not null)
            {
                Walk(current.InnerException);
            }
        }

        Walk(exception);
        return builder.ToString();
    }

    private static string[] ZipNames(string path)
    {
        string backups = Path.Combine(path, "backups");
        return Directory.Exists(backups) ? [.. Directory.GetFiles(backups, "wishlistarr-*.zip").Select(Path.GetFileName)!] : [];
    }

    private static string OnlyZip(string path)
    {
        string backups = Path.Combine(path, "backups");
        return Directory.GetFiles(backups, "wishlistarr-*.zip").Single();
    }

    private static string NewestZip(string path)
    {
        string backups = Path.Combine(path, "backups");
        return Directory.GetFiles(backups, "wishlistarr-*.zip").OrderBy(Path.GetFileName, StringComparer.Ordinal).Last();
    }

    private static string DatabasePath(string path) => Path.Combine(path, "wishlistarr.db");

    private static string TempDirectory() => Directory.CreateTempSubdirectory("wishlistarr-backup").FullName;

    private static string TempChild(string path, string name)
    {
        string child = Path.Combine(path, name);
        Directory.CreateDirectory(child);
        return child;
    }

    private static void DeleteDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        for (int attempt = 0; attempt < 5 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
