using Wishlistarr.Api.Settings;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Tmdb;

public sealed class SettingsTmdbApiKeyProvider(IServiceScopeFactory scopes) : ITmdbApiKeyProvider
{
    public async Task<string?> GetApiKeyAsync(CancellationToken ct = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SettingsStore>().GetAsync(TmdbOptions.ApiKeySetting, ct);
    }
}
