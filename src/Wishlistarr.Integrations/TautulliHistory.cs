using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Wishlistarr.Integrations.Tautulli;

public sealed record WatchEvent(string RatingKey, string TitleKey, string Guid, bool Episode, bool Finished, int PercentComplete, long Date);

public sealed class TautulliHistory(IHttpClientFactory clients, IConfiguration configuration)
{
    public const string ClientName = "Wishlistarr.Tautulli";

    public async Task<IReadOnlyList<WatchEvent>?> ReadAsync(string? userId, string? serverId, CancellationToken ct = default)
    {
        string? address = configuration["Tautulli:Url"];
        string? apiKey = configuration["Tautulli:ApiKey"];
        if (!long.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out long user) || user <= 0
            || string.IsNullOrEmpty(serverId) || string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl)
            || !Wishlistarr.Integrations.Arr.ArrSettings.TryNormalizeUrl(address, out Uri url))
        {
            return null;
        }

        try
        {
            JsonElement server = await this.GetAsync(url, apiKey, "get_server_info", ct);
            if (!server.TryGetProperty("pms_identifier", out JsonElement identifier) || identifier.GetString() != serverId)
            {
                return null;
            }

            List<WatchEvent> events = [];
            const int pageSize = 100;
            for (int start = 0; start < 1000; start += pageSize)
            {
                JsonElement page = await this.GetAsync(url, apiKey, $"get_history&user_id={user.ToString(CultureInfo.InvariantCulture)}&grouping=0&include_activity=0&start={start.ToString(CultureInfo.InvariantCulture)}&length={pageSize.ToString(CultureInfo.InvariantCulture)}&order_column=date&order_dir=desc", ct);
                JsonElement data = page.GetProperty("data");
                foreach (JsonElement row in data.EnumerateArray())
                {
                    if (ReadLong(row, "user_id") != user)
                    {
                        return null;
                    }

                    string? type = row.GetProperty("media_type").GetString();
                    string keyName = type == "episode" ? "grandparent_rating_key" : "rating_key";
                    if (type is not ("movie" or "episode") || ReadLong(row, keyName) is not long key || key <= 0
                        || ReadLong(row, "rating_key") is not long itemKey || itemKey <= 0
                        || !row.TryGetProperty("guid", out JsonElement guid) || guid.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(guid.GetString()))
                    {
                        continue;
                    }

                    long? percent = ReadLong(row, "percent_complete");
                    decimal? watched = ReadDecimal(row, "watched_status");
                    long? date = ReadLong(row, "date");
                    if (percent is null || watched is not (0 or 0.25m or 0.5m or 0.75m or 1) || date is null)
                    {
                        return null;
                    }

                    events.Add(new WatchEvent(itemKey.ToString(CultureInfo.InvariantCulture), key.ToString(CultureInfo.InvariantCulture), guid.GetString()!, type == "episode", watched == 1, (int)Math.Clamp(percent.Value, 0, 100), date.Value));
                }

                if (data.GetArrayLength() < pageSize)
                {
                    break;
                }
            }

            return events;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private static decimal? ReadDecimal(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out decimal number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number) => number,
            _ => null,
        };
    }

    private static long? ReadLong(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long number) => number,
            _ => null,
        };
    }

    private async Task<JsonElement> GetAsync(Uri url, string key, string command, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(url, $"api/v2?cmd={command}"));
        request.Headers.Add("X-Api-Key", key);
        using HttpResponseMessage response = await clients.CreateClient(ClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct) ?? throw new JsonException();
        JsonElement envelope = document.RootElement.GetProperty("response");
        if (envelope.GetProperty("result").GetString() != "success") throw new JsonException();
        return envelope.GetProperty("data").Clone();
    }
}
