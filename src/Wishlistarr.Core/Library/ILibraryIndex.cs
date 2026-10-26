using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Library;

public interface ILibraryIndex
{
    // Null means the index cannot answer yet, for example before its first sync, and the caller should ask the server.
    Task<IReadOnlyList<MediaItem>?> FindAsync(MediaKind kind, ExternalId id, CancellationToken ct = default);

    // Null means the index cannot answer yet. Titles that are not in the library are left out.
    Task<IndexedGuids?> FindGuidsAsync(IReadOnlyCollection<TitleKey> titles, CancellationToken ct = default);
}

public sealed record IndexedGuids(string ServerId, IReadOnlyDictionary<TitleKey, IReadOnlyList<string>> Guids);
