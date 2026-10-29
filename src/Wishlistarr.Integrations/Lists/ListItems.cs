using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Lists;

public sealed record ListItem(MediaKind Kind, int TmdbId);

public sealed class ListRateLimitedException(TimeSpan? delay, DateTimeOffset? retryAt) : HttpRequestException("The list provider rate limited the request.", null, HttpStatusCode.TooManyRequests)
{
    public TimeSpan? Delay { get; } = delay;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}

public static class ListItems
{
    public static async Task<IReadOnlyList<ListItem>> TraktAsync(HttpClient http, long listId, string clientId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(listId);
        using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.trakt.tv/lists/{listId.ToString(CultureInfo.InvariantCulture)}/items?limit=100&page=1");
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", clientId);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        EnsureSuccess(response);
        using JsonDocument document = await response.Content.ReadFromJsonAsync<JsonDocument>(ct) ?? throw new JsonException();
        List<ListItem> items = [];
        foreach (JsonElement row in document.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("type", out JsonElement type))
            {
                continue;
            }
            string? name = type.GetString();
            MediaKind? kind = name switch { "movie" => MediaKind.Movie, "show" => MediaKind.Show, _ => null };
            if (kind is not null && row.TryGetProperty(name!, out JsonElement title)
                && title.TryGetProperty("ids", out JsonElement ids)
                && ids.TryGetProperty("tmdb", out JsonElement id)
                && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int tmdbId) && tmdbId > 0)
            {
                items.Add(new ListItem(kind.Value, tmdbId));
            }
        }

        return items.Distinct().ToList();
    }

    public static async Task<IReadOnlyList<ListItem>> MdbListAsync(HttpClient http, long listId, string apiKey, bool external = false, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(listId);
        string key = Uri.EscapeDataString(apiKey);
        string prefix = external ? "external/lists" : "lists";
        using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.mdblist.com/{prefix}/{listId.ToString(CultureInfo.InvariantCulture)}/items?apikey={key}&limit=100");
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        EnsureSuccess(response);
        using JsonDocument document = await response.Content.ReadFromJsonAsync<JsonDocument>(ct) ?? throw new JsonException();
        List<(int Rank, ListItem Item)> items = [];
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || (!document.RootElement.TryGetProperty("movies", out _) && !document.RootElement.TryGetProperty("shows", out _)))
        {
            throw new JsonException();
        }
        ReadBucket("movies", MediaKind.Movie);
        ReadBucket("shows", MediaKind.Show);
        return items.OrderBy(item => item.Rank).Select(item => item.Item).Distinct().ToList();

        void ReadBucket(string property, MediaKind kind)
        {
            if (!document.RootElement.TryGetProperty(property, out JsonElement bucket))
            {
                return;
            }
            foreach (JsonElement row in bucket.EnumerateArray())
            {
                if (row.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int tmdbId) && tmdbId > 0)
                {
                    int rank = row.TryGetProperty("rank", out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed) ? parsed : int.MaxValue;
                    items.Add((rank, new ListItem(kind, tmdbId)));
                }
            }
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ListRateLimitedException(response.Headers.RetryAfter?.Delta, response.Headers.RetryAfter?.Date);
        }
        response.EnsureSuccessStatusCode();
    }
}
