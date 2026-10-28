using System.Net;
using System.Text.RegularExpressions;

namespace Wishlistarr.Integrations.Tmdb;

public sealed partial class TmdbImageProxy(HttpClient http, TmdbCache cache)
{
    public const int MaxBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> AllowedSizes =
    [
        "w45", "w92", "w154", "w185", "w300", "w342", "w500", "w780", "w1280", "h632", "original",
    ];

    private static readonly HashSet<string> AllowedContentTypes = ["image/jpeg", "image/png"];

    public static bool IsAllowed(string size, string file) =>
        AllowedSizes.Contains(size) && FilePattern().IsMatch(file);

    public async Task<TmdbImageContent?> FetchAsync(string size, string file, CancellationToken ct = default)
    {
        if (!IsAllowed(size, file))
        {
            throw new ArgumentException("The image size or path is not allowed.");
        }

        string cacheKey = $"{size}/{file}";
        if (cache.TryGetImage(cacheKey, out TmdbImageContent? cached, out long generation)) return cached;

        using HttpResponseMessage response = await http.GetAsync(
            new Uri($"{size}/{file}", UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        string? contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (!response.IsSuccessStatusCode || contentType is null || !AllowedContentTypes.Contains(contentType))
        {
            throw new TmdbException("The image could not be retrieved.", response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            throw new TmdbException("The image is too large.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new TmdbException("The image is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        TmdbImageContent image = new(buffer.ToArray(), contentType);
        cache.SetImage(cacheKey, image, generation);
        return image;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,100}\.(jpg|jpeg|png)\z", RegexOptions.CultureInvariant)]
    private static partial Regex FilePattern();
}

public sealed record TmdbImageContent(byte[] Bytes, string ContentType);
