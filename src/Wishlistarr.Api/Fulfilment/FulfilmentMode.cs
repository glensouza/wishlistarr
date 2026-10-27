using Wishlistarr.Api.Settings;
using Wishlistarr.Core.Fulfilment;

namespace Wishlistarr.Api.Fulfilment;

public sealed class FulfilmentMode(SettingsStore store)
{
    public async Task<FulfilmentBackend?> GetAsync(CancellationToken ct = default) =>
        FulfilmentModes.TryParse(await store.GetAsync(FulfilmentModes.SettingKey, ct), out FulfilmentBackend? backend) ? backend : null;
}
