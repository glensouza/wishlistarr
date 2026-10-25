using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Backup;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Backup;

public sealed class StandaloneBackups : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    public StandaloneBackups(string path, int retention = BackupOptions.DefaultRetention, TimeProvider? time = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataDirectory.EnvironmentVariable] = path })
            .Build();
        this.Directory = DataDirectory.Resolve(configuration, path);
        ServiceCollection services = new();
        services.AddDbContext<WishlistarrDbContext>(options => options.UseWishlistarrSqlite(this.Directory.DatabasePath));
        this.provider = services.BuildServiceProvider();
        this.Service = new BackupService(
            this.Directory,
            Options.Create(new BackupOptions { Retention = retention }),
            this.provider.GetRequiredService<IServiceScopeFactory>(),
            time ?? TimeProvider.System,
            NullLogger<BackupService>.Instance);
    }

    public DataDirectory Directory { get; }

    public BackupService Service { get; }

    public async ValueTask DisposeAsync()
    {
        await this.provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }
}

public sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public static class BackupTestSupport
{
    public const string ApplicationName = "Wishlistarr";

    public static string TempDirectory() => Directory.CreateTempSubdirectory("wishlistarr-backup").FullName;

    public static void DeleteDirectory(string path)
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
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    public static byte[] KeyXml() =>
        """
        <?xml version="1.0" encoding="utf-8"?>
        <key id="11111111-1111-1111-1111-111111111111" version="1" />
        """u8.ToArray();

    public static byte[] Zip(params (string Name, byte[] Content)[] entries) =>
        Zip(CompressionLevel.NoCompression, entries);

    public static byte[] Zip(CompressionLevel level, params (string Name, byte[] Content)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, level);
                using Stream output = entry.Open();
                output.Write(content);
            }
        }

        return stream.ToArray();
    }

    public static byte[] ZipWithAttributes(string name, int externalAttributes, params (string Name, byte[] Content)[] others)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string otherName, byte[] content) in others)
            {
                ZipArchiveEntry other = archive.CreateEntry(otherName, CompressionLevel.NoCompression);
                using Stream output = other.Open();
                output.Write(content);
            }

            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            entry.ExternalAttributes = externalAttributes;
            using Stream target = entry.Open();
            target.Write(KeyXml());
        }

        return stream.ToArray();
    }

    public static byte[] RawStoredZip(params (string Name, byte[] Content)[] entries)
    {
        using MemoryStream body = new();
        using MemoryStream central = new();
        foreach ((string name, byte[] content) in entries)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            int offset = (int)body.Length;
            WriteHeader(body, 0x04034b50, nameBytes, content.Length, central: false, offset);
            body.Write(content);
            WriteHeader(central, 0x02014b50, nameBytes, content.Length, central: true, offset);
        }

        int centralOffset = (int)body.Length;
        central.Position = 0;
        central.CopyTo(body);
        Span<byte> end = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)entries.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)entries.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)central.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], (uint)centralOffset);
        body.Write(end);
        return body.ToArray();
    }

    public static byte[] WithDeclaredLength(byte[] zip, string name, uint length)
    {
        byte[] copy = (byte[])zip.Clone();
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        int patched = 0;
        for (int index = 0; index + 46 < copy.Length; index++)
        {
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(index));
            if (signature == 0x04034b50 && NameMatches(copy, index + 26, index + 30, nameBytes))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(index + 22), length);
                patched++;
            }
            else if (signature == 0x02014b50 && NameMatches(copy, index + 28, index + 46, nameBytes))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(index + 24), length);
                patched++;
            }
        }

        Assert.Equal(2, patched);
        return copy;
    }

    public static async Task CreatePlainDatabaseAsync(string databasePath)
    {
        await using SqliteConnection connection = new($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE example (id INTEGER PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<string?> DecryptedSettingAsync(string dataPath, string key)
    {
        string keys = Path.Combine(dataPath, "keys");
        string database = Path.Combine(dataPath, DataDirectory.DatabaseFileName);
        if (!Directory.Exists(keys) || !File.Exists(database))
        {
            return null;
        }

        string cipher;
        await using (SqliteConnection connection = new($"Data Source={database};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT \"Value\" FROM \"Settings\" WHERE \"Key\" = @key;";
            command.Parameters.AddWithValue("@key", key);
            cipher = Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
        }

        string copy = TempDirectory();
        try
        {
            CopyDirectory(keys, copy);
            IDataProtector protector = DataProtectionProvider
                .Create(new DirectoryInfo(copy), builder => builder.SetApplicationName(ApplicationName))
                .CreateProtector(SettingsStore.ProtectorPurpose);
            try
            {
                return protector.Unprotect(cipher);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
        finally
        {
            DeleteDirectory(copy);
        }
    }

    private static bool NameMatches(byte[] zip, int lengthOffset, int nameOffset, byte[] name)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(lengthOffset)) != name.Length || nameOffset + name.Length > zip.Length)
        {
            return false;
        }

        return zip.AsSpan(nameOffset, name.Length).SequenceEqual(name);
    }

    private static void WriteHeader(Stream stream, uint signature, byte[] name, int size, bool central, int offset)
    {
        byte[] header = new byte[central ? 46 : 30];
        Span<byte> span = header;
        BinaryPrimitives.WriteUInt32LittleEndian(span, signature);
        int shift = central ? 2 : 0;
        if (central)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 20);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(span[(4 + shift)..], 20);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(18 + shift)..], (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(22 + shift)..], (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(26 + shift)..], (ushort)name.Length);
        if (central)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[42..], (uint)offset);
        }

        stream.Write(header);
        stream.Write(name);
    }
}
