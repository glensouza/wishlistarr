using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Library;

public interface ILibraryIndex
{
    // Null means the index cannot answer yet, for example before its first sync, and the caller should ask the server.
    Task<IReadOnlyList<MediaItem>?> FindAsync(MediaKind kind, ExternalId id, CancellationToken ct = default);

    // Null means the index cannot answer yet. A title the index does not hold is missing from the dictionary.
    Task<IReadOnlyDictionary<TitleKey, IReadOnlyList<MediaItem>>?> FindManyByTmdbAsync(IReadOnlyCollection<TitleKey> titles, CancellationToken ct = default);
}
