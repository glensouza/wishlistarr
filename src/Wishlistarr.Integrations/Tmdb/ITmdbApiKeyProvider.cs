namespace Wishlistarr.Integrations.Tmdb;

public interface ITmdbApiKeyProvider
{
    Task<string?> GetApiKeyAsync(CancellationToken ct = default);
}
