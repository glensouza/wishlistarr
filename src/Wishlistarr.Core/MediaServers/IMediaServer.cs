namespace Wishlistarr.Core.MediaServers;

public interface IMediaServer
{
    Task<MediaServerTestResult> TestConnectionAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaServerUser>> GetUsersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaItem>> FindByExternalIdAsync(MediaKind kind, ExternalId id, CancellationToken ct = default);

    // Plex only documents the section guid filter for its own agents, so any other guid returns nothing rather than relying on an undocumented filter.
    Task<IReadOnlyList<MediaItem>> FindByGuidAsync(string guid, CancellationToken ct = default);

    Task<MediaItemLabels?> GetLabelsAsync(string itemId, CancellationToken ct = default);

    Task SetLabelsAsync(MediaItemLabels item, IReadOnlyList<string> labels, CancellationToken ct = default);
}
