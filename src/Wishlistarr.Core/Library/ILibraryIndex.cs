using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Library;

public interface ILibraryIndex
{
    // Null means the index cannot answer yet, for example before its first sync, and the caller should ask the server.
    Task<IReadOnlyList<MediaItem>?> FindAsync(MediaKind kind, ExternalId id, CancellationToken ct = default);

    // Null means the index cannot answer yet. An empty list means it can, and nothing matched.
    Task<IReadOnlyList<LibraryCatalogEntry>?> ListRecentAsync(int limit, CancellationToken ct = default);
}

public sealed record LibraryCatalogEntry(MediaKind Kind, int TmdbId, string Title, int? Year);
