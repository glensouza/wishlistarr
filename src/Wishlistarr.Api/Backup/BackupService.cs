using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Core;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Backup;

public sealed class BackupService(
    DataDirectory directory,
    IOptions<BackupOptions> options,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<BackupService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int sequence;

    public BackupList List()
    {
        List<BackupItem> backups = [];
        if (Directory.Exists(directory.BackupsPath))
        {
            backups.AddRange(new DirectoryInfo(directory.BackupsPath)
                .EnumerateFiles()
                .Select(file => ToItem(file))
                .Where(item => item is not null)
                .Cast<BackupItem>()
                .OrderByDescending(item => item.Name, StringComparer.Ordinal));
        }

        return new BackupList(options.Value.Retention, this.RestorePending, backups);
    }

    public bool RestorePending => File.Exists(this.PendingReadyPath);

    public string? ResolveDownload(string name)
    {
        if (!BackupNames.IsBackupFileName(name))
        {
            return null;
        }

        string root = Path.GetFullPath(directory.BackupsPath);
        string path = Path.GetFullPath(Path.Combine(root, name));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(path))
        {
            return null;
        }

        return path;
    }

    public async Task<BackupItem> CreateBackupAsync(CancellationToken ct = default)
    {
        await this.gate.WaitAsync(ct);
        try
        {
            return await this.CreateBackupCoreAsync(ct);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task BackupIfMigrationsPendingAsync(WishlistarrDbContext db, CancellationToken ct = default)
    {
        // Opening SQLite creates the file, so a missing database is a new install and has nothing to back up.
        if (!File.Exists(directory.DatabasePath))
        {
            return;
        }

        List<string> pending = [.. await db.Database.GetPendingMigrationsAsync(ct)];
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            await this.CreateBackupAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Pre-migration backup failed. Migrations were not applied.");
            throw new InvalidOperationException(
                "Pending migrations were not applied because the pre-migration backup failed.",
                exception);
        }
    }

    public async Task StageRestoreAsync(Stream upload, long? declaredLength, CancellationToken ct = default)
    {
        long maxBytes = options.Value.MaxUploadBytes;
        if (declaredLength > maxBytes)
        {
            throw new BackupRejectedException(BackupRejection.TooLarge);
        }

        string staging = Path.Combine(directory.Root, "restore-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string zipPath = Path.Combine(staging, "upload.zip");
            await CopyLimitedAsync(upload, zipPath, maxBytes, ct);
            string extracted = Path.Combine(staging, "extracted");
            Directory.CreateDirectory(extracted);
            await BackupArchive.ExtractAsync(zipPath, extracted, maxBytes, ct);
            File.Delete(zipPath);
            await this.ValidateDatabaseAsync(Path.Combine(extracted, BackupNames.DatabaseEntryName), ct);
            this.ValidateKeyDirectory(Path.Combine(extracted, "keys"));
            await File.WriteAllTextAsync(Path.Combine(extracted, BackupNames.ReadyFileName), "ready", ct);

            await this.gate.WaitAsync(ct);
            try
            {
                if (Directory.Exists(directory.RestorePendingPath))
                {
                    Directory.Delete(directory.RestorePendingPath, true);
                }

                Directory.Move(extracted, directory.RestorePendingPath);
            }
            finally
            {
                this.gate.Release();
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public async Task ApplyPendingRestoreAsync(CancellationToken ct = default)
    {
        if (!this.RestorePending)
        {
            return;
        }

        string stagedDatabase = Path.Combine(directory.RestorePendingPath, BackupNames.DatabaseEntryName);
        string stagedKeys = Path.Combine(directory.RestorePendingPath, "keys");
        if (!await this.StagedIsValidAsync(stagedDatabase, stagedKeys, ct))
        {
            logger.LogError("Pending restore failed validation and was not applied.");
            throw new InvalidOperationException("A pending restore failed validation and was not applied.");
        }

        if (File.Exists(directory.DatabasePath))
        {
            await this.CreateBackupAsync(ct);
        }

        this.SwapIn(stagedDatabase, stagedKeys);
        logger.LogInformation("Pending restore applied.");
    }

    private async Task<BackupItem> CreateBackupCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(directory.DatabasePath))
        {
            throw new InvalidOperationException("There is no database to back up.");
        }

        string[] keyFiles = this.KeyFiles();
        Directory.CreateDirectory(directory.BackupsPath);
        this.DeletePartials();
        string name = this.NextName();
        string zipPath = Path.Combine(directory.BackupsPath, name);
        string partial = zipPath + ".partial";
        string snapshot = Path.Combine(directory.BackupsPath, "." + name + ".db");
        try
        {
            await SqliteSnapshot.VacuumIntoAsync(directory.DatabasePath, snapshot, ct);
            BackupArchive.Write(partial, snapshot, keyFiles);
            File.Move(partial, zipPath);
        }
        catch
        {
            DeleteIfExists(partial);
            DeleteIfExists(zipPath);
            throw;
        }
        finally
        {
            DeleteIfExists(snapshot);
        }

        try
        {
            this.ApplyRetention();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Old backups could not be deleted.");
        }

        logger.LogInformation("Database backup {BackupName} completed.", name);
        return ToItem(new FileInfo(zipPath)) ?? throw new InvalidOperationException("The backup file could not be read.");
    }

    private void SwapIn(string stagedDatabase, string stagedKeys)
    {
        string previousDatabase = directory.DatabasePath + ".previous";
        string previousWal = directory.DatabasePath + "-wal.previous";
        string previousShm = directory.DatabasePath + "-shm.previous";
        string previousKeys = directory.KeysPath + ".previous";
        if (File.Exists(directory.DatabasePath))
        {
            DeleteIfExists(previousDatabase);
            DeleteIfExists(previousWal);
            DeleteIfExists(previousShm);
        }

        if (Directory.Exists(directory.KeysPath) && Directory.Exists(previousKeys))
        {
            Directory.Delete(previousKeys, true);
        }

        bool retiredDatabase = MoveIfExists(directory.DatabasePath, previousDatabase);
        bool retiredWal = MoveIfExists(directory.DatabasePath + "-wal", previousWal);
        bool retiredShm = MoveIfExists(directory.DatabasePath + "-shm", previousShm);
        bool retiredKeys = false;
        if (Directory.Exists(directory.KeysPath))
        {
            Directory.Move(directory.KeysPath, previousKeys);
            retiredKeys = true;
        }

        try
        {
            File.Move(stagedDatabase, directory.DatabasePath);
            Directory.Move(stagedKeys, directory.KeysPath);
        }
        catch
        {
            if (!File.Exists(directory.DatabasePath) && File.Exists(previousDatabase))
            {
                File.Move(previousDatabase, directory.DatabasePath);
                MoveIfExists(previousWal, directory.DatabasePath + "-wal");
                MoveIfExists(previousShm, directory.DatabasePath + "-shm");
            }

            if (!Directory.Exists(directory.KeysPath) && Directory.Exists(previousKeys))
            {
                Directory.Move(previousKeys, directory.KeysPath);
            }

            throw;
        }

        DeleteIfExists(this.PendingReadyPath);
        if (retiredDatabase)
        {
            DeleteIfExists(previousDatabase);
        }

        if (retiredWal)
        {
            DeleteIfExists(previousWal);
        }

        if (retiredShm)
        {
            DeleteIfExists(previousShm);
        }

        if (retiredKeys && Directory.Exists(previousKeys))
        {
            Directory.Delete(previousKeys, true);
        }

        if (Directory.Exists(directory.RestorePendingPath))
        {
            Directory.Delete(directory.RestorePendingPath, true);
        }
    }

    private async Task<bool> StagedIsValidAsync(string databasePath, string keysPath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(databasePath) || new FileInfo(databasePath).Length > options.Value.MaxUploadBytes)
            {
                return false;
            }

            this.ValidateKeyDirectory(keysPath);
            await this.ValidateDatabaseAsync(databasePath, ct);
            return this.PendingHasOnlyExpectedFiles();
        }
        catch (BackupRejectedException)
        {
            return false;
        }
    }

    private bool PendingHasOnlyExpectedFiles()
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory.RestorePendingPath))
        {
            string name = Path.GetFileName(entry);
            if (name is not (BackupNames.DatabaseEntryName or BackupNames.ReadyFileName or "keys"))
            {
                return false;
            }
        }

        return true;
    }

    private async Task ValidateDatabaseAsync(string databasePath, CancellationToken ct)
    {
        if (!await SqliteSnapshot.IntegrityOkAsync(databasePath, ct))
        {
            throw new BackupRejectedException(BackupRejection.IntegrityFailed);
        }

        IReadOnlyList<string>? applied = await SqliteSnapshot.ReadMigrationsAsync(databasePath, ct);
        if (applied is null)
        {
            throw new BackupRejectedException(BackupRejection.SchemaUnreadable);
        }

        HashSet<string> known = this.KnownMigrations();
        if (applied.Any(id => !known.Contains(id)))
        {
            throw new BackupRejectedException(BackupRejection.SchemaNewer);
        }
    }

    private void ValidateKeyDirectory(string keysPath)
    {
        if (!Directory.Exists(keysPath) || Directory.EnumerateDirectories(keysPath).Any())
        {
            throw new BackupRejectedException(BackupRejection.MissingKeyRing);
        }

        List<string> files = [.. Directory.EnumerateFiles(keysPath)];
        if (files.Count == 0
            || files.Any(file => new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint))
            || files.Any(file => !BackupNames.IsKeyFileName(Path.GetFileName(file))))
        {
            throw new BackupRejectedException(BackupRejection.MissingKeyRing);
        }
    }

    private HashSet<string> KnownMigrations()
    {
        using IServiceScope scope = scopes.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        return db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
    }

    private string[] KeyFiles()
    {
        if (!Directory.Exists(directory.KeysPath))
        {
            throw new InvalidOperationException("The data protection key ring is missing, so the backup was not created.");
        }

        string[] files = Directory.GetFiles(directory.KeysPath, "*.xml", SearchOption.TopDirectoryOnly);
        if (files.Length == 0
            || files.Any(file => new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint))
            || files.Any(file => !BackupNames.IsKeyFileName(Path.GetFileName(file))))
        {
            throw new InvalidOperationException("The data protection key ring is missing, so the backup was not created.");
        }

        return files;
    }

    private void ApplyRetention()
    {
        List<FileInfo> files = [.. new DirectoryInfo(directory.BackupsPath)
            .EnumerateFiles()
            .Where(file => BackupNames.IsBackupFileName(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)];
        foreach (FileInfo extra in files.Skip(options.Value.Retention))
        {
            extra.Delete();
        }
    }

    private void DeletePartials()
    {
        foreach (string partial in Directory.EnumerateFiles(directory.BackupsPath, "*.partial"))
        {
            if (BackupNames.IsBackupFileName(Path.GetFileName(partial[..^".partial".Length])))
            {
                DeleteIfExists(partial);
            }
        }
    }

    private string NextName()
    {
        string stamp = time.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            this.sequence++;
            string name = "wishlistarr-" + stamp + "-" + this.sequence.ToString("D6", CultureInfo.InvariantCulture) + ".zip";
            if (!File.Exists(Path.Combine(directory.BackupsPath, name)))
            {
                return name;
            }
        }

        throw new InvalidOperationException("A backup file name could not be chosen.");
    }

    private string PendingReadyPath => Path.Combine(directory.RestorePendingPath, BackupNames.ReadyFileName);

    private static BackupItem? ToItem(FileInfo file)
    {
        DateTimeOffset? createdAt = BackupNames.CreatedAt(file.Name);
        return createdAt is null ? null : new BackupItem(file.Name, createdAt.Value, file.Length);
    }

    private static async Task CopyLimitedAsync(Stream source, string destination, long maxBytes, CancellationToken ct)
    {
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new BackupRejectedException(BackupRejection.TooLarge);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static bool MoveIfExists(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return false;
        }

        File.Move(source, destination);
        return true;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

public sealed record BackupItem(string Name, DateTimeOffset CreatedAt, long SizeBytes);

public sealed record BackupList(int Retention, bool RestorePending, IReadOnlyList<BackupItem> Backups);
