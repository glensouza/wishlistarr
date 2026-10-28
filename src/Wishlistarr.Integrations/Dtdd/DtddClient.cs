using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Dtdd;

public sealed class DtddClient(HttpClient http, DtddCache cache, IDtddApiKeyProvider keys, IOptions<DtddOptions> options) : IDtddClient
{
    public async Task<IReadOnlyList<string>> GetWarningsAsync(MediaKind kind, int tmdbId, CancellationToken ct = default)
    {
        if (cache.TryGet(kind, tmdbId, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        string key = await this.RequireKeyAsync(ct);
        (HttpStatusCode status, string body) = await this.GetAsync($"items?tmdb={tmdbId.ToString(CultureInfo.InvariantCulture)}", key, ct);
        if (status == HttpStatusCode.NotFound)
        {
            return this.Remember(kind, tmdbId, []);
        }

        if (status != HttpStatusCode.OK)
        {
            throw Failure(status, body);
        }

        List<DtddListItem> items = Read<List<DtddListItem>>(body) ?? [];
        if (items.Count == 0)
        {
            return this.Remember(kind, tmdbId, []);
        }

        int wanted = kind == MediaKind.Movie ? options.Value.MovieTypeId : options.Value.TvTypeId;
        DtddListItem? match = null;
        bool recognized = false;
        foreach (DtddListItem item in items)
        {
            int? typeId = item.ItemTypeId;
            if (typeId != options.Value.MovieTypeId && typeId != options.Value.TvTypeId)
            {
                continue;
            }

            recognized = true;
            if (match is null && typeId == wanted)
            {
                match = item;
            }
        }

        if (!recognized)
        {
            throw new DtddException("DoesTheDogDie returned an unrecognized item list.");
        }

        if (match is null)
        {
            return this.Remember(kind, tmdbId, []);
        }

        (HttpStatusCode detailStatus, string detailBody) = await this.GetAsync($"items/{match.Id.ToString(CultureInfo.InvariantCulture)}", key, ct);
        if (detailStatus != HttpStatusCode.OK)
        {
            throw Failure(detailStatus, detailBody);
        }

        DtddItemDetail? detail = Read<DtddItemDetail>(detailBody);
        IReadOnlyList<string> names = Select(detail?.TopicItemStats);
        return this.Remember(kind, tmdbId, names);
    }

    public async Task<bool> TestApiKeyAsync(CancellationToken ct = default)
    {
        string key = await this.RequireKeyAsync(ct);
        (HttpStatusCode status, string body) = await this.GetAsync("items?tmdb=0", key, ct);
        if (status is HttpStatusCode.OK or HttpStatusCode.NotFound)
        {
            return true;
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return false;
        }

        throw Failure(status, body);
    }

    internal static IReadOnlyList<string> Select(IReadOnlyList<DtddTopicStat>? stats)
    {
        if (stats is null || stats.Count == 0)
        {
            return [];
        }

        Dictionary<string, (string Name, int Yes)> best = new(StringComparer.OrdinalIgnoreCase);
        foreach (DtddTopicStat stat in stats)
        {
            string? name = stat.TopicName?.Trim();
            int yes = stat.YesSum ?? 0;
            int no = stat.NoSum ?? 0;
            if (string.IsNullOrEmpty(name) || yes <= no || yes <= 0)
            {
                continue;
            }

            if (!best.TryGetValue(name, out (string Name, int Yes) existing) || yes > existing.Yes)
            {
                best[name] = (name, yes);
            }
        }

        return [.. best.Values
            .OrderByDescending(item => item.Yes)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Name)];
    }

    private IReadOnlyList<string> Remember(MediaKind kind, int tmdbId, IReadOnlyList<string> names)
    {
        cache.Set(kind, tmdbId, names);
        return names;
    }

    private async Task<string> RequireKeyAsync(CancellationToken ct) =>
        await keys.GetApiKeyAsync(ct) is { Length: > 0 } key ? key : throw new DtddNotConfiguredException();

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string relative, string key, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, relative);
        request.Headers.TryAddWithoutValidation("X-API-KEY", key);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, body);
    }

    private static DtddException Failure(HttpStatusCode status, string body)
    {
        string? code = ErrorCode(body);
        string message = code is null
            ? $"DoesTheDogDie answered {(int)status}."
            : $"DoesTheDogDie answered {(int)status} ({code}).";
        return new DtddException(message, status);
    }

    private static string? ErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DtddError>(body)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? Read<T>(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new DtddException("DoesTheDogDie returned an empty response.");
        }

        return JsonSerializer.Deserialize<T>(body);
    }
}
