using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Library;

public interface ILibraryIndex
{
    // Null means the index cannot answer yet, for example before its first sync, and the caller should ask the server.
    Task<IReadOnlyList<MediaItem>?> FindAsync(MediaKind kind, ExternalId id, CancellationToken ct = default);
}
