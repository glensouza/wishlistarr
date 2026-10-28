namespace Wishlistarr.Core.MediaServers;

public interface IMediaServerTrailers
{
    Task<bool> HasTrailerAsync(string itemId, CancellationToken ct = default);

    Task<HttpResponseMessage?> OpenTrailerAsync(string itemId, string? range, CancellationToken ct = default);
}
