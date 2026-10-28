namespace Wishlistarr.Integrations.Dtdd;

public interface IDtddApiKeyProvider
{
    Task<string?> GetApiKeyAsync(CancellationToken ct = default);
}
