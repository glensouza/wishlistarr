using Wishlistarr.Api.Settings;
using Wishlistarr.Integrations.Dtdd;

namespace Wishlistarr.Api.Tmdb;

public sealed class SettingsDtddApiKeyProvider(IServiceScopeFactory scopes) : IDtddApiKeyProvider
{
    public async Task<string?> GetApiKeyAsync(CancellationToken ct = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SettingsStore>().GetAsync(DtddOptions.ApiKeySetting, ct);
    }
}
