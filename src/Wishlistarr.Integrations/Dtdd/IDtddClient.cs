using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Dtdd;

public interface IDtddClient
{
    Task<IReadOnlyList<string>> GetWarningsAsync(MediaKind kind, int tmdbId, CancellationToken ct = default);

    Task<bool> TestApiKeyAsync(CancellationToken ct = default);
}
