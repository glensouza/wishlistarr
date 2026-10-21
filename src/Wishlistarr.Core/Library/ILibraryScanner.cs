using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Library;

public sealed record LibraryEntry(MediaItem Item, IReadOnlyList<string> Labels, DateTimeOffset? UpdatedAt);

public sealed class LibraryScanReport
{
    // Set to false while the entries are read when the server returned fewer items than it announced.
    public bool Complete { get; set; } = true;
}

public sealed record LibraryScan(string ServerId, IAsyncEnumerable<LibraryEntry> Entries)
{
    public LibraryScanReport Report { get; init; } = new();
}

public interface ILibraryScanner
{
    // A null updatedSince reads every movie and show. Otherwise only items changed after that time are read; callers
    // step the bound back to allow for servers that treat it as exclusive.
    Task<LibraryScan> ScanAsync(DateTimeOffset? updatedSince, CancellationToken ct = default);

    Task<string?> GetServerIdAsync(CancellationToken ct = default);

    Task<LibrarySnapshot?> GetEntryAsync(string itemId, CancellationToken ct = default);
}

public sealed record LibrarySnapshot(string ServerId, LibraryEntry Entry);
