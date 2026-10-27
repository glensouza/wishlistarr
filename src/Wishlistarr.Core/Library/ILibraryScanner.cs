using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Library;

// Labels is null when the source does not carry them, as in section listings, so the stored labels are left alone.
public sealed record LibraryEntry(MediaItem Item, IReadOnlyList<string>? Labels, DateTimeOffset? UpdatedAt, DateTimeOffset? AddedAt = null);

public sealed class LibraryScanReport
{
    // Set to false while the entries are read when the server returned fewer items than it announced.
    public bool Complete { get; set; } = true;
}

public sealed record LibraryScan(string ServerId, IAsyncEnumerable<LibraryEntry> Entries)
{
    public LibraryScanReport Report { get; init; } = new();
}

public sealed record LibraryLabelScan(string ServerId, IReadOnlyDictionary<string, IReadOnlyList<string>> LabelsByItem, bool Complete)
{
    public IReadOnlyDictionary<string, LabeledItem> Items { get; init; } = new Dictionary<string, LabeledItem>(StringComparer.Ordinal);
}

public sealed record LabeledItem(string Guid, string Title);

public interface ILibraryScanner
{
    // A null updatedSince reads every movie and show. Otherwise only items changed strictly after that time are read,
    // so callers step the bound back to cover items changed in the same second.
    Task<LibraryScan> ScanAsync(DateTimeOffset? updatedSince, CancellationToken ct = default);

    // Reads which items carry which labels without depending on when items last changed.
    Task<LibraryLabelScan> ScanLabelsAsync(CancellationToken ct = default);

    Task<string?> GetServerIdAsync(CancellationToken ct = default);

    Task<LibrarySnapshot?> GetEntryAsync(string itemId, CancellationToken ct = default);
}

public sealed record LibrarySnapshot(string ServerId, LibraryEntry Entry);
