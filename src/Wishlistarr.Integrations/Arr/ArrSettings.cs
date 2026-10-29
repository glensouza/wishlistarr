using System.Globalization;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.Settings;

namespace Wishlistarr.Integrations.Arr;

public static class ArrSettings
{
    public static bool IsManagedKey(string key) =>
        key.StartsWith(KeyPrefix(ArrService.Sonarr), StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(KeyPrefix(ArrService.Radarr), StringComparison.OrdinalIgnoreCase);

    public static string KeyPrefix(ArrService service) => service == ArrService.Sonarr ? "sonarr." : "radarr.";

    public static bool TryNormalizeUrl(string? value, out Uri url)
    {
        url = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 500)
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || parsed.UserInfo.Length > 0
            || parsed.Host.Length == 0
            || parsed.Query.Length > 0
            || parsed.Fragment.Length > 0)
        {
            return false;
        }

        UriBuilder builder = new(parsed);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        url = builder.Uri;
        return true;
    }

    internal static string Url(ArrService service) => KeyPrefix(service) + "url";

    internal static string ApiKey(ArrService service) => KeyPrefix(service) + "api_key";

    internal static string QualityProfileId(ArrService service) => KeyPrefix(service) + "quality_profile_id";

    internal static string RootFolder(ArrService service) => KeyPrefix(service) + "root_folder";

    internal static string TagIds(ArrService service) => KeyPrefix(service) + "tag_ids";
}

public sealed class ArrConnectionStore(IAppSettings settings)
{
    public async Task<ArrSettingsView> GetViewAsync(ArrService service, CancellationToken ct = default)
    {
        string? url = await settings.GetAsync(ArrSettings.Url(service), ct);
        string? key = await settings.GetAsync(ArrSettings.ApiKey(service), ct);
        return new ArrSettingsView(
            url,
            !string.IsNullOrEmpty(key),
            ParseInt(await settings.GetAsync(ArrSettings.QualityProfileId(service), ct)),
            NullIfEmpty(await settings.GetAsync(ArrSettings.RootFolder(service), ct)),
            ParseIds(await settings.GetAsync(ArrSettings.TagIds(service), ct)));
    }

    public async Task<ArrConnection?> GetAsync(ArrService service, CancellationToken ct = default)
    {
        ArrSettingsView view = await this.GetViewAsync(service, ct);
        string? key = await settings.GetAsync(ArrSettings.ApiKey(service), ct);
        if (!ArrSettings.TryNormalizeUrl(view.Url, out Uri url) || string.IsNullOrEmpty(key))
        {
            return null;
        }

        return new ArrConnection(url, key, view.QualityProfileId, view.RootFolderPath, view.TagIds);
    }

    public async Task<ArrConnection?> CandidateAsync(ArrService service, Uri url, string? apiKey, CancellationToken ct = default)
    {
        if (apiKey is null)
        {
            ArrConnection? saved = await this.GetAsync(service, ct);
            apiKey = saved?.Url == url ? saved.ApiKey : null;
        }

        return string.IsNullOrEmpty(apiKey) ? null : new ArrConnection(url, apiKey, null, null, []);
    }

    public async Task SaveAsync(
        ArrService service,
        Uri url,
        string? apiKey,
        int? qualityProfileId,
        string? rootFolderPath,
        IReadOnlyList<int> tagIds,
        CancellationToken ct = default)
    {
        await settings.SetAsync(ArrSettings.Url(service), url.AbsoluteUri, false, ct);
        if (!string.IsNullOrEmpty(apiKey))
        {
            await settings.SetAsync(ArrSettings.ApiKey(service), apiKey, true, ct);
        }

        await this.SetOptionalAsync(ArrSettings.QualityProfileId(service), qualityProfileId?.ToString(CultureInfo.InvariantCulture), ct);
        await this.SetOptionalAsync(ArrSettings.RootFolder(service), rootFolderPath, ct);
        await this.SetOptionalAsync(ArrSettings.TagIds(service), tagIds.Count == 0 ? null : string.Join(',', tagIds), ct);
    }

    public async Task DeleteAsync(ArrService service, CancellationToken ct = default)
    {
        foreach (string key in new[]
        {
            ArrSettings.Url(service),
            ArrSettings.ApiKey(service),
            ArrSettings.QualityProfileId(service),
            ArrSettings.RootFolder(service),
            ArrSettings.TagIds(service),
        })
        {
            await settings.DeleteAsync(key, ct);
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;

    private static List<int> ParseIds(string? value) =>
        [.. (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseInt)
            .Where(id => id is > 0)
            .Select(id => id!.Value)];

    private async Task SetOptionalAsync(string key, string? value, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(value))
        {
            await settings.DeleteAsync(key, ct);
        }
        else
        {
            await settings.SetAsync(key, value, false, ct);
        }
    }
}
