using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Library;

public interface ILibraryIndex
{
    // Null means the index cannot answer yet, for example before its first sync, and the caller should ask the server.
    Task<IReadOnlyList<MediaItem>?> FindAsync(MediaKind kind, ExternalId id, CancellationToken ct = default);

    // Null means the index cannot answer yet. An empty list means it can, and nothing matched.
    Task<IReadOnlyList<LibraryCatalogEntry>?> ListRecentAsync(int limit, CancellationToken ct = default);

    // Null means the index cannot answer yet. Titles that are not in the library are left out.
    Task<IndexedGuids?> FindGuidsAsync(IReadOnlyCollection<TitleKey> titles, CancellationToken ct = default);

    // Null means the index cannot answer yet. A title the index does not hold is missing from the dictionary.
    Task<IReadOnlyDictionary<TitleKey, IReadOnlyList<MediaItem>>?> FindManyByTmdbAsync(IReadOnlyCollection<TitleKey> titles, CancellationToken ct = default);
}

public sealed record IndexedGuids(string ServerId, IReadOnlyDictionary<TitleKey, IReadOnlyList<string>> Guids);

public sealed record LibraryCatalogEntry(MediaKind Kind, int TmdbId);
