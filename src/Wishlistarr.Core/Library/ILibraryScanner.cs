using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Library;

public sealed record LibraryEntry(MediaItem Item, IReadOnlyList<string> Labels, DateTimeOffset? UpdatedAt);

public sealed record LibraryScan(string ServerId, IAsyncEnumerable<LibraryEntry> Entries);

public interface ILibraryScanner
{
    // A null updatedSince reads every movie and show; otherwise only items changed at or after that time.
    Task<LibraryScan> ScanAsync(DateTimeOffset? updatedSince, CancellationToken ct = default);

    Task<string?> GetServerIdAsync(CancellationToken ct = default);

    Task<LibrarySnapshot?> GetEntryAsync(string itemId, CancellationToken ct = default);
}

public sealed record LibrarySnapshot(string ServerId, LibraryEntry Entry);
