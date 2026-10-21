using System.Globalization;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.Settings;
using Wishlistarr.Integrations.Arr;

namespace Wishlistarr.Integrations.Seerr;

public static class SeerrSettings
{
    public const string KeyPrefix = "seerr.";

    public static bool IsManagedKey(string key) => key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryNormalizeUrl(string? value, out Uri url) => ArrSettings.TryNormalizeUrl(value, out url);

    internal const string UrlKey = KeyPrefix + "url";

    internal const string ApiKeyKey = KeyPrefix + "api_key";

    internal const string UserIdKey = KeyPrefix + "user_id";
}

public sealed class SeerrConnectionStore(IAppSettings settings)
{
    public async Task<SeerrSettingsView> GetViewAsync(CancellationToken ct = default)
    {
        string? url = await settings.GetAsync(SeerrSettings.UrlKey, ct);
        string? key = await settings.GetAsync(SeerrSettings.ApiKeyKey, ct);
        return new SeerrSettingsView(url, !string.IsNullOrEmpty(key), ParseUserId(await settings.GetAsync(SeerrSettings.UserIdKey, ct)));
    }

    public async Task<SeerrConnection?> GetAsync(CancellationToken ct = default)
    {
        SeerrSettingsView view = await this.GetViewAsync(ct);
        string? key = await settings.GetAsync(SeerrSettings.ApiKeyKey, ct);
        if (!SeerrSettings.TryNormalizeUrl(view.Url, out Uri url) || string.IsNullOrEmpty(key) || view.UserId is not > 0)
        {
            return null;
        }

        return new SeerrConnection(url, key, view.UserId.Value);
    }

    public async Task SaveAsync(Uri url, string? apiKey, int userId, CancellationToken ct = default)
    {
        await settings.SetAsync(SeerrSettings.UrlKey, url.AbsoluteUri, false, ct);
        if (!string.IsNullOrEmpty(apiKey))
        {
            await settings.SetAsync(SeerrSettings.ApiKeyKey, apiKey, true, ct);
        }

        await settings.SetAsync(SeerrSettings.UserIdKey, userId.ToString(CultureInfo.InvariantCulture), false, ct);
    }

    public async Task DeleteAsync(CancellationToken ct = default)
    {
        await settings.DeleteAsync(SeerrSettings.UrlKey, ct);
        await settings.DeleteAsync(SeerrSettings.ApiKeyKey, ct);
        await settings.DeleteAsync(SeerrSettings.UserIdKey, ct);
    }

    private static int? ParseUserId(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id > 0 ? id : null;
}
