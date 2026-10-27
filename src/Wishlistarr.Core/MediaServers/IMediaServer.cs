namespace Wishlistarr.Core.MediaServers;

public interface IMediaServer
{
    Task<MediaServerTestResult> TestConnectionAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaServerUser>> GetUsersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaItem>> FindByExternalIdAsync(MediaKind kind, ExternalId id, CancellationToken ct = default);

    // Only Plex agent guids (plex://) can be searched; any other guid returns no items without asking the server.
    Task<IReadOnlyList<MediaItem>> FindByGuidAsync(string guid, CancellationToken ct = default);

    Task<MediaItemLabels?> GetLabelsAsync(string itemId, CancellationToken ct = default);

    Task SetLabelsAsync(MediaItemLabels item, IReadOnlyList<string> labels, CancellationToken ct = default);
}
