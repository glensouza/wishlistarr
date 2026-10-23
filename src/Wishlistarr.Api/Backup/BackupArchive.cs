using System.IO.Compression;

namespace Wishlistarr.Api.Backup;

public static class BackupArchive
{
    public static void Write(string zipPath, string databasePath, IReadOnlyList<string> keyFiles)
    {
        using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(databasePath, BackupNames.DatabaseEntryName, CompressionLevel.Optimal);
        foreach (string keyFile in keyFiles)
        {
            archive.CreateEntryFromFile(
                keyFile,
                BackupNames.KeysEntryPrefix + Path.GetFileName(keyFile),
                CompressionLevel.Optimal);
        }
    }

    public static async Task ExtractAsync(string zipPath, string destination, long maxBytes, CancellationToken ct)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException)
        {
            throw new BackupRejectedException(BackupRejection.NotAZip);
        }
        catch (IOException)
        {
            throw new BackupRejectedException(BackupRejection.NotAZip);
        }

        using (archive)
        {
            RejectEntries(archive, maxBytes);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName is "keys" or "keys/")
                {
                    continue;
                }

                string relative = entry.FullName == BackupNames.DatabaseEntryName
                    ? BackupNames.DatabaseEntryName
                    : "keys/" + Path.GetFileName(entry.FullName);
                string target = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
                string root = Path.GetFullPath(destination);
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new BackupRejectedException(BackupRejection.UnsafePath);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyEntryAsync(entry, target, maxBytes, ct);
            }
        }
    }

    private static void RejectEntries(ZipArchive archive, long maxBytes)
    {
        if (archive.Entries.Count is 0 or > 64)
        {
            throw new BackupRejectedException(archive.Entries.Count == 0
                ? BackupRejection.NotAZip
                : BackupRejection.UnexpectedEntry);
        }

        bool database = false;
        int keys = 0;
        long uncompressed = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!IsSafeEntry(entry.FullName) || entry.Length < 0)
            {
                throw new BackupRejectedException(BackupRejection.UnsafePath);
            }

            uncompressed += entry.Length;
            if (uncompressed > maxBytes)
            {
                throw new BackupRejectedException(BackupRejection.TooLarge);
            }

            if (entry.FullName == BackupNames.DatabaseEntryName)
            {
                database = entry.Length > 0;
            }
            else if (entry.FullName is "keys" or "keys/")
            {
                continue;
            }
            else if (IsKeyEntry(entry.FullName))
            {
                keys++;
            }
            else
            {
                throw new BackupRejectedException(BackupRejection.UnexpectedEntry);
            }
        }

        if (!database)
        {
            throw new BackupRejectedException(BackupRejection.MissingDatabase);
        }

        if (keys == 0)
        {
            throw new BackupRejectedException(BackupRejection.MissingKeyRing);
        }
    }

    private static bool IsKeyEntry(string name)
    {
        if (!name.StartsWith(BackupNames.KeysEntryPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string fileName = name[BackupNames.KeysEntryPrefix.Length..];
        return fileName.Length > 0 && !fileName.Contains('/') && BackupNames.IsKeyFileName(fileName);
    }

    private static bool IsSafeEntry(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 200 || name.Contains('\\') || name.Contains(':') || name[0] == '/')
        {
            return false;
        }

        foreach (string part in name.Split('/'))
        {
            if (part is "..")
            {
                return false;
            }
        }

        return true;
    }

    private static async Task CopyEntryAsync(ZipArchiveEntry entry, string target, long maxBytes, CancellationToken ct)
    {
        await using Stream input = entry.Open();
        await using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return;
            }

            written += read;
            if (written > maxBytes)
            {
                throw new BackupRejectedException(BackupRejection.TooLarge);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
