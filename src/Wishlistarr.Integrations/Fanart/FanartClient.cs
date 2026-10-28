using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Integrations.Fanart;

public sealed class FanartClient(HttpClient http, TmdbCache cache)
{
    public const string ApiKeySetting = "fanart.api_key";
    public const string ClientKeySetting = "fanart.client_key";

    public async Task<FanartArtwork?> GetArtworkAsync(bool movie, int id, string? apiKey, string? clientKey = null, CancellationToken ct = default)
    {
        if (id <= 0 || (string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(clientKey))) return null;
        List<string> query = [];
        if (!string.IsNullOrWhiteSpace(apiKey)) query.Add($"api_key={Uri.EscapeDataString(apiKey)}");
        if (!string.IsNullOrWhiteSpace(clientKey)) query.Add($"client_key={Uri.EscapeDataString(clientKey)}");
        string path = $"v3/{(movie ? "movies" : "tv")}/{id}?{string.Join('&', query)}";
        using HttpResponseMessage response = await http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using JsonDocument document = await response.Content.ReadFromJsonAsync<JsonDocument>(ct)
            ?? throw new HttpRequestException("fanart.tv returned an empty response.");
        JsonElement root = document.RootElement;
        return new FanartArtwork(
            FirstImage(root, movie ? ["moviebackground"] : ["showbackground"]),
            FirstImage(root, movie ? ["hdmovielogo", "movielogo"] : ["hdtvlogo", "clearlogo"]));
    }

    public async Task<TmdbImageContent?> GetImageAsync(string url, string size, CancellationToken ct = default)
    {
        if (!IsAllowedUrl(url) || size is not ("full" or "preview" or "bigpreview")) throw new ArgumentException("The image is not allowed.");
        string path = new Uri(url).AbsolutePath;
        string cacheKey = $"fanart/{size}/{path}";
        if (cache.TryGetImage(cacheKey, out TmdbImageContent? cached, out long generation)) return cached;
        Uri asset = new($"https://assets.fanart.tv/{(size == "full" ? "fanart" : size)}/{path[8..]}");
        using HttpResponseMessage response = await http.GetAsync(asset, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        string? contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (contentType is not ("image/jpeg" or "image/png") || response.Content.Headers.ContentLength > TmdbImageProxy.MaxBytes)
            throw new HttpRequestException("The image is not valid.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > TmdbImageProxy.MaxBytes) throw new HttpRequestException("The image is too large.");
            buffer.Write(chunk, 0, read);
        }

        TmdbImageContent image = new(buffer.ToArray(), contentType);
        cache.SetImage(cacheKey, image, generation);
        return image;
    }

    private static string? FirstImage(JsonElement root, string[] fields)
    {
        foreach (string field in fields)
        {
            if (!root.TryGetProperty(field, out JsonElement images) || images.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement image in images.EnumerateArray())
            {
                if (image.ValueKind == JsonValueKind.Object && image.TryGetProperty("url", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String && IsAllowedUrl(value.GetString()))
                {
                    return value.GetString();
                }
            }
        }

        return null;
    }

    public static bool IsAllowedUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "assets.fanart.tv" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath.StartsWith("/fanart/", StringComparison.Ordinal)
        && uri.AbsolutePath.Length <= 500
        && (uri.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
}

public sealed record FanartArtwork(string? Background, string? Logo);
