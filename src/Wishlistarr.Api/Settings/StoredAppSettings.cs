using Wishlistarr.Core.Settings;

namespace Wishlistarr.Api.Settings;

public sealed class StoredAppSettings(IServiceScopeFactory scopes) : IAppSettings
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SettingsStore>().GetAsync(key, ct);
    }

    public async Task SetAsync(string key, string value, bool isSecret, CancellationToken ct = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SettingsStore>().SetAsync(key, value, isSecret, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SettingsStore>().DeleteAsync(key, ct);
    }
}
