namespace Wishlistarr.Core.MediaServers;

public interface IMediaServerAccount
{
    Task<MediaServerAccountStatus> GetStatusAsync(CancellationToken ct = default);

    Task<MediaServerLinkStart> StartLinkAsync(CancellationToken ct = default);

    Task<MediaServerLinkCheck?> CheckLinkAsync(string linkId, CancellationToken ct = default);

    Task UnlinkAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DiscoveredMediaServer>> GetServersAsync(CancellationToken ct = default);

    Task<SelectedMediaServer?> SelectServerAsync(string serverId, CancellationToken ct = default);
}
