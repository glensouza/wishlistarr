namespace Wishlistarr.Core.MediaServers;

public interface IMediaServer
{
    Task<MediaServerTestResult> TestConnectionAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaServerUser>> GetUsersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaItem>> FindByExternalIdAsync(MediaKind kind, ExternalId id, CancellationToken ct = default);
}
