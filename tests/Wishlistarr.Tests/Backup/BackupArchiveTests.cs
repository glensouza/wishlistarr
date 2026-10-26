using System.IO.Compression;
using System.Text;
using Wishlistarr.Api.Backup;
using static Wishlistarr.Tests.Backup.BackupTestSupport;

namespace Wishlistarr.Tests.Backup;

public sealed class BackupArchiveTests : IDisposable
{
    private const long Limit = 1024 * 1024;

    private static readonly byte[] Database = "database"u8.ToArray();

    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (string root in this.roots)
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task A_complete_archive_extracts()
    {
        string extracted = await this.ExtractAsync(Zip(Valid()));

        Assert.True(File.Exists(Path.Combine(extracted, BackupNames.DatabaseEntryName)));
        Assert.True(File.Exists(Path.Combine(extracted, "keys", "key-1.xml")));
    }

    [Fact]
    public async Task Parent_directory_traversal_is_rejected()
    {
        await this.AssertRejectedAsync(Zip(Valid(("keys/../../escape.xml", KeyXml()))), BackupRejection.UnsafePath);
        await this.AssertRejectedAsync(Zip(Valid(("../escape.txt", KeyXml()))), BackupRejection.UnsafePath);
    }

    [Fact]
    public async Task Backslash_names_are_rejected() => await this.AssertRejectedAsync(Zip(Valid(("keys\\key-2.xml", KeyXml()))), BackupRejection.UnsafePath);

    [Fact]
    public async Task Drive_prefixed_names_are_rejected() => await this.AssertRejectedAsync(Zip(Valid(("C:x", KeyXml()))), BackupRejection.UnsafePath);

    [Fact]
    public async Task Absolute_names_are_rejected() => await this.AssertRejectedAsync(Zip(Valid(("/x", KeyXml()))), BackupRejection.UnsafePath);

    [Fact]
    public async Task Duplicate_names_are_rejected()
    {
        await this.AssertRejectedAsync(Zip(Valid(("keys/key-1.xml", KeyXml()))), BackupRejection.DuplicateEntry);
        await this.AssertRejectedAsync(Zip(Valid(("KEYS/KEY-1.XML", KeyXml()))), BackupRejection.DuplicateEntry);
    }

    [Fact]
    public async Task Empty_names_are_rejected() => await this.AssertRejectedAsync(RawStoredZip(Valid((string.Empty, KeyXml()))), BackupRejection.UnsafePath);

    [Fact]
    public async Task Symlink_entries_are_rejected()
    {
        const int symlink = 0xA1FF << 16;

        await this.AssertRejectedAsync(
            ZipWithAttributes("keys/key-2.xml", symlink, Valid()),
            BackupRejection.LinkEntry);
    }

    [Fact]
    public async Task Reparse_point_entries_are_rejected()
    {
        await this.AssertRejectedAsync(
            ZipWithAttributes("keys/key-2.xml", (int)FileAttributes.ReparsePoint, Valid()),
            BackupRejection.LinkEntry);
    }

    [Fact]
    public async Task Regular_unix_file_modes_are_accepted()
    {
        const int regular = 0x81A4 << 16;

        await this.ExtractAsync(ZipWithAttributes("keys/key-2.xml", regular, Valid()));
    }

    [Fact]
    public async Task Missing_database_entry_is_rejected() => await this.AssertRejectedAsync(Zip(("keys/key-1.xml", KeyXml())), BackupRejection.MissingDatabase);

    [Fact]
    public async Task Missing_key_ring_entry_is_rejected() => await this.AssertRejectedAsync(Zip((BackupNames.DatabaseEntryName, Database)), BackupRejection.MissingKeyRing);

    [Fact]
    public async Task Total_uncompressed_size_is_capped_across_entries()
    {
        byte[] half = new byte[600];
        byte[] zip = Zip((BackupNames.DatabaseEntryName, half), ("keys/key-1.xml", half));

        await this.AssertRejectedAsync(zip, BackupRejection.TooLarge, maxBytes: 1000);
    }

    [Fact]
    public async Task Declared_sizes_over_the_cap_are_rejected_before_any_data_is_read()
    {
        byte[] zip = Zip((BackupNames.DatabaseEntryName, Database), ("keys/key-1.xml", KeyXml()));
        zip = WithDeclaredLength(zip, BackupNames.DatabaseEntryName, 600);
        zip = WithDeclaredLength(zip, "keys/key-1.xml", 600);

        string extracted = await this.AssertRejectedAsync(zip, BackupRejection.TooLarge, maxBytes: 1000);

        Assert.Empty(Directory.GetFileSystemEntries(extracted));
    }

    [Fact]
    public async Task Streamed_bytes_over_the_cap_are_rejected_when_the_declared_size_is_small()
    {
        string root = this.Root();
        await using MemoryStream stream = new(new byte[5000]);

        BackupRejectedException rejected = await Assert.ThrowsAsync<BackupRejectedException>(
            () => BackupArchive.CopyLimitedAsync(stream, Path.Combine(root, "entry"), declaredLength: 100, declaredCrc: 0, remaining: 1000, default));

        Assert.Equal(BackupRejection.TooLarge, rejected.Rejection);
    }

    [Fact]
    public async Task Streamed_bytes_over_the_declared_size_are_rejected_when_the_cap_is_not_reached()
    {
        string root = this.Root();
        await using MemoryStream stream = new(new byte[5000]);

        BackupRejectedException rejected = await Assert.ThrowsAsync<BackupRejectedException>(
            () => BackupArchive.CopyLimitedAsync(stream, Path.Combine(root, "entry"), declaredLength: 100, declaredCrc: 0, remaining: 100000, default));

        Assert.Equal(BackupRejection.NotAZip, rejected.Rejection);
    }

    [Fact]
    public async Task The_entry_cap_follows_the_configured_value()
    {
        byte[] zip = Zip(Valid(("keys/key-2.xml", KeyXml()), ("keys/key-3.xml", KeyXml())));

        await this.AssertRejectedAsync(zip, BackupRejection.TooManyEntries, maxEntries: 3);
        await this.ExtractAsync(zip, maxEntries: 4);
    }

    [Fact]
    public void Verify_reports_an_entry_whose_bytes_no_longer_match_its_checksum()
    {
        string root = this.Root();
        string database = Path.Combine(root, "snapshot.db");
        string key = Path.Combine(root, "key-1.xml");
        File.WriteAllBytes(database, Database);
        File.WriteAllBytes(key, KeyXml());
        string zipPath = Path.Combine(root, "backup.zip");
        byte[] zip = Zip((BackupNames.DatabaseEntryName, Database), ("keys/key-1.xml", KeyXml()));
        File.WriteAllBytes(zipPath, zip);
        BackupArchive.Verify(zipPath, database, [key]);

        int at = zip.AsSpan().IndexOf(KeyXml());
        zip[at + 10] ^= 0x01;
        File.WriteAllBytes(zipPath, zip);

        Assert.Throws<InvalidDataException>(() => BackupArchive.Verify(zipPath, database, [key]));
    }

    [Fact]
    public async Task Entry_count_is_capped()
    {
        List<(string, byte[])> entries = [(BackupNames.DatabaseEntryName, Database)];
        for (int index = 0; index < BackupOptions.DefaultMaxEntries; index++)
        {
            entries.Add(($"keys/key-{index}.xml", KeyXml()));
        }

        await this.AssertRejectedAsync(Zip([.. entries]), BackupRejection.TooManyEntries);
        await this.ExtractAsync(Zip([.. entries.Take(BackupOptions.DefaultMaxEntries)]));
    }

    [Fact]
    public async Task An_entry_larger_than_its_declared_length_is_rejected()
    {
        byte[] key = new byte[4000];
        KeyXml().CopyTo(key, 0);
        byte[] zip = WithDeclaredLength(Zip(CompressionLevel.Optimal, Valid(("keys/key-2.xml", key))), "keys/key-2.xml", 100);

        string extracted = await this.AssertRejectedAsync(zip, BackupRejection.NotAZip);

        string written = Path.Combine(extracted, "keys", "key-2.xml");
        Assert.True(!File.Exists(written) || new FileInfo(written).Length <= 100);
    }

    [Fact]
    public async Task An_entry_shorter_than_its_declared_length_is_rejected()
    {
        byte[] zip = WithDeclaredLength(Zip(CompressionLevel.Optimal, Valid(("keys/key-2.xml", KeyXml()))), "keys/key-2.xml", 5000);

        await this.AssertRejectedAsync(zip, BackupRejection.NotAZip);
    }

    [Fact]
    public async Task A_highly_compressed_entry_that_lies_about_its_size_stops_early()
    {
        byte[] bomb = new byte[20 * 1024 * 1024];
        byte[] zip = WithDeclaredLength(Zip(CompressionLevel.Optimal, Valid(("keys/key-2.xml", bomb))), "keys/key-2.xml", 64);

        string extracted = await this.AssertRejectedAsync(zip, BackupRejection.NotAZip);

        string written = Path.Combine(extracted, "keys", "key-2.xml");
        Assert.True(!File.Exists(written) || new FileInfo(written).Length <= 64);
    }

    [Fact]
    public async Task Corrupt_compressed_data_is_rejected_as_not_a_zip()
    {
        byte[] database = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 4000).Select(row => $"row {row} value {row * 7};")));
        byte[] zip = Zip(CompressionLevel.Optimal, (BackupNames.DatabaseEntryName, database), ("keys/key-1.xml", KeyXml()));
        for (int index = 200; index < 4000; index++)
        {
            zip[index] ^= 0x5A;
        }

        await this.AssertRejectedAsync(zip, BackupRejection.NotAZip);
    }

    private static (string, byte[])[] Valid(params (string Name, byte[] Content)[] extra) =>
        [(BackupNames.DatabaseEntryName, Database), ("keys/key-1.xml", KeyXml()), .. extra];

    private async Task<string> ExtractAsync(byte[] zip, long maxBytes = Limit, int maxEntries = BackupOptions.DefaultMaxEntries)
    {
        string root = this.Root();
        string zipPath = Path.Combine(root, "upload.zip");
        string extracted = Path.Combine(root, "extracted");
        Directory.CreateDirectory(extracted);
        await File.WriteAllBytesAsync(zipPath, zip);
        await BackupArchive.ExtractAsync(zipPath, extracted, maxEntries, maxBytes, default);
        return extracted;
    }

    private async Task<string> AssertRejectedAsync(byte[] zip, BackupRejection expected, long maxBytes = Limit, int maxEntries = BackupOptions.DefaultMaxEntries)
    {
        string root = this.Root();
        string zipPath = Path.Combine(root, "upload.zip");
        string extracted = Path.Combine(root, "extracted");
        Directory.CreateDirectory(extracted);
        await File.WriteAllBytesAsync(zipPath, zip);

        BackupRejectedException rejected = await Assert.ThrowsAsync<BackupRejectedException>(
            () => BackupArchive.ExtractAsync(zipPath, extracted, maxEntries, maxBytes, default));

        Assert.Equal(expected, rejected.Rejection);
        Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escape.txt")));
        return extracted;
    }

    private string Root()
    {
        string root = TempDirectory();
        this.roots.Add(root);
        return root;
    }
}
