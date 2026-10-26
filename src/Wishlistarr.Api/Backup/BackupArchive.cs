using System.IO.Compression;

namespace Wishlistarr.Api.Backup;

public static class BackupArchive
{
    private const int UnixFileTypeMask = 0xF000;

    private const int UnixRegularFile = 0x8000;

    private const int UnixDirectory = 0x4000;

    public static void Write(string zipPath, string databasePath, IReadOnlyList<string> keyFiles)
    {
        using (FileStream stream = new(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                archive.CreateEntryFromFile(databasePath, BackupNames.DatabaseEntryName, CompressionLevel.Optimal);
                foreach (string keyFile in keyFiles)
                {
                    archive.CreateEntryFromFile(
                        keyFile,
                        BackupNames.KeysEntryPrefix + Path.GetFileName(keyFile),
                        CompressionLevel.Optimal);
                }
            }

            stream.Flush(flushToDisk: true);
        }

        Verify(zipPath, databasePath, keyFiles);
    }

    public static async Task ExtractAsync(string zipPath, string destination, int maxEntries, long maxBytes, CancellationToken ct)
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
            RejectEntries(archive, maxEntries, maxBytes);
            long total = 0;
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
                try
                {
                    total += await CopyEntryAsync(entry, target, maxBytes - total, ct);
                }
                catch (InvalidDataException)
                {
                    throw new BackupRejectedException(BackupRejection.NotAZip);
                }
            }
        }
    }

    internal static void Verify(string zipPath, string databasePath, IReadOnlyList<string> keyFiles)
    {
        Dictionary<string, long> expected = new(StringComparer.Ordinal)
        {
            [BackupNames.DatabaseEntryName] = new FileInfo(databasePath).Length,
        };
        foreach (string keyFile in keyFiles)
        {
            expected[BackupNames.KeysEntryPrefix + Path.GetFileName(keyFile)] = new FileInfo(keyFile).Length;
        }

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count != expected.Count
            || archive.Entries.Any(entry => !expected.TryGetValue(entry.FullName, out long length) || entry.Length != length))
        {
            throw new InvalidDataException("The backup zip did not read back as written.");
        }

        byte[] buffer = new byte[81920];
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using Stream stream = entry.Open();
            long length = 0;
            uint crc = 0;
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                length += read;
                crc = ZipCrc32.Append(crc, buffer.AsSpan(0, read));
            }

            if (length != entry.Length || crc != entry.Crc32)
            {
                throw new InvalidDataException("The backup zip did not read back as written.");
            }
        }
    }

    private static void RejectEntries(ZipArchive archive, int maxEntries, long maxBytes)
    {
        if (archive.Entries.Count == 0)
        {
            throw new BackupRejectedException(BackupRejection.NotAZip);
        }

        if (archive.Entries.Count > maxEntries)
        {
            throw new BackupRejectedException(BackupRejection.TooManyEntries);
        }

        bool database = false;
        int keys = 0;
        long uncompressed = 0;
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!IsSafeEntry(entry.FullName) || entry.Length < 0)
            {
                throw new BackupRejectedException(BackupRejection.UnsafePath);
            }

            if (IsLinkOrSpecial(entry))
            {
                throw new BackupRejectedException(BackupRejection.LinkEntry);
            }

            if (!names.Add(entry.FullName.TrimEnd('/')))
            {
                throw new BackupRejectedException(BackupRejection.DuplicateEntry);
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

    // Zip tools record a Unix file mode in the high half of the external attributes and Windows
    // attributes in the low half. Only plain files and directories are accepted, so a symlink or
    // reparse point cannot make the restore read or write outside the backup.
    private static bool IsLinkOrSpecial(ZipArchiveEntry entry)
    {
        int unixType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (unixType is not (0 or UnixRegularFile or UnixDirectory))
        {
            return true;
        }

        return ((FileAttributes)(entry.ExternalAttributes & 0xFFFF)).HasFlag(FileAttributes.ReparsePoint);
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

    // The sizes in a zip header are whatever the file's author wrote. The reader stops at the
    // declared length, so the length and checksum are both checked to catch an entry that was cut short.
    private static async Task<long> CopyEntryAsync(ZipArchiveEntry entry, string target, long remaining, CancellationToken ct)
    {
        await using Stream input = entry.Open();
        return await CopyLimitedAsync(input, target, entry.Length, entry.Crc32, remaining, ct);
    }

    // The byte budget is checked before the declared length, so an entry that streams past the
    // total cap is reported as too large however small its header claims to be.
    internal static async Task<long> CopyLimitedAsync(
        Stream input,
        string target,
        long declaredLength,
        uint declaredCrc,
        long remaining,
        CancellationToken ct)
    {
        await using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        long written = 0;
        uint crc = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            written += read;
            if (written > remaining)
            {
                throw new BackupRejectedException(BackupRejection.TooLarge);
            }

            if (written > declaredLength)
            {
                throw new BackupRejectedException(BackupRejection.NotAZip);
            }

            crc = ZipCrc32.Append(crc, buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (written != declaredLength || crc != declaredCrc)
        {
            throw new BackupRejectedException(BackupRejection.NotAZip);
        }

        return written;
    }
}
