using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Tmdb;

public sealed class TmdbClient(HttpClient http, TmdbCache cache, ITmdbApiKeyProvider keys) : ITmdbClient
{
    private static readonly TimeSpan SearchLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ListLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan DetailLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan CertificationLifetime = TimeSpan.FromDays(7);

    public async Task<bool> TestApiKeyAsync(CancellationToken ct = default)
    {
        string key = await this.RequireKeyAsync(ct);
        using HttpRequestMessage request = CreateRequest("configuration", [], key);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TmdbException($"TMDB answered {(int)response.StatusCode}.", response.StatusCode);
        }

        return true;
    }

    public Task<TmdbPage<TmdbSearchResult>> SearchAsync(string query, TmdbMediaType? type = null, int page = 1, string language = "en-US", CancellationToken ct = default)
    {
        string path = type is null ? "search/multi" : $"search/{Segment(type.Value)}";
        return this.GetAsync<TmdbPage<TmdbSearchResult>>(
            path,
            [("query", query), ("page", Number(page)), ("language", language)],
            SearchLifetime,
            ct);
    }

    public Task<TmdbDetails> GetDetailsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) =>
        this.GetAsync<TmdbDetails>($"{Segment(type)}/{id}", [("language", language)], DetailLifetime, ct);

    public async Task<IReadOnlyList<TmdbKeyword>> GetKeywordsAsync(TmdbMediaType type, int id, CancellationToken ct = default)
    {
        KeywordsResponse response = await this.GetAsync<KeywordsResponse>($"{Segment(type)}/{id}/keywords", [], DetailLifetime, ct);
        return ReadKeywords(type == TmdbMediaType.Movie ? response.Keywords : response.Results);
    }

    public Task<TmdbCredits> GetCreditsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) =>
        this.GetAsync<TmdbCredits>($"{Segment(type)}/{id}/credits", [("language", language)], DetailLifetime, ct);

    public Task<TmdbVideos> GetVideosAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) =>
        this.GetAsync<TmdbVideos>($"{Segment(type)}/{id}/videos", [("language", language)], DetailLifetime, ct);

    public Task<TmdbImages> GetImagesAsync(TmdbMediaType type, int id, string imageLanguages = "en,null", CancellationToken ct = default) =>
        this.GetAsync<TmdbImages>($"{Segment(type)}/{id}/images", [("include_image_language", imageLanguages)], DetailLifetime, ct);

    public Task<TmdbPage<TmdbSearchResult>> DiscoverAsync(TmdbMediaType type, TmdbDiscoverQuery query, string language = "en-US", CancellationToken ct = default)
    {
        List<(string, string)> parameters = [("language", language), ("page", Number(query.Page))];
        AddIfPresent(parameters, "with_genres", query.Genres);
        AddIfPresent(parameters, "certification", query.WithCertification);
        AddIfPresent(parameters, "certification_country", query.CertificationCountry);
        AddIfPresent(parameters, "certification.lte", query.CertificationLessThanOrEqual);
        AddIfPresent(parameters, "sort_by", query.SortBy);
        return this.GetAsync<TmdbPage<TmdbSearchResult>>($"discover/{Segment(type)}", parameters, ListLifetime, ct);
    }

    public Task<TmdbPage<TmdbSearchResult>> GetTrendingAsync(TmdbMediaType type, string window = "week", int page = 1, string language = "en-US", CancellationToken ct = default)
    {
        if (window is not ("day" or "week"))
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The window must be 'day' or 'week'.");
        }

        return this.GetAsync<TmdbPage<TmdbSearchResult>>(
            $"trending/{Segment(type)}/{window}",
            [("page", Number(page)), ("language", language)],
            ListLifetime,
            ct);
    }

    public Task<TmdbWatchProviders> GetWatchProvidersAsync(TmdbMediaType type, int id, CancellationToken ct = default) =>
        this.GetAsync<TmdbWatchProviders>($"{Segment(type)}/{id}/watch/providers", [], DetailLifetime, ct);

    public async Task<int?> FindIdAsync(TmdbMediaType type, ExternalIdSource source, string externalId, CancellationToken ct = default)
    {
        string? externalSource = source switch
        {
            ExternalIdSource.Tvdb => "tvdb_id",
            ExternalIdSource.Imdb => "imdb_id",
            _ => null,
        };
        if (externalSource is null)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Only TVDB and IMDb ids can be looked up.");
        }

        FindResponse response = await this.GetAsync<FindResponse>(
            $"find/{Uri.EscapeDataString(externalId)}",
            [("external_source", externalSource)],
            DetailLifetime,
            ct);
        IReadOnlyList<FindResult>? results = type == TmdbMediaType.Movie ? response.MovieResults : response.TvResults;
        return results is { Count: > 0 } ? results[0].Id : null;
    }

    public async Task<IReadOnlyList<TmdbListedGenre>> GetGenresAsync(string language = "en-US", CancellationToken ct = default)
    {
        GenresResponse movies = await this.GetAsync<GenresResponse>("genre/movie/list", [("language", language)], DetailLifetime, ct);
        GenresResponse shows = await this.GetAsync<GenresResponse>("genre/tv/list", [("language", language)], DetailLifetime, ct);
        HashSet<int> movieIds = [.. (movies.Genres ?? []).Select(genre => genre.Id)];
        HashSet<int> showIds = [.. (shows.Genres ?? []).Select(genre => genre.Id)];
        return [.. (movies.Genres ?? []).Concat(shows.Genres ?? [])
            .Where(genre => !string.IsNullOrWhiteSpace(genre.Name))
            .GroupBy(genre => genre.Id)
            .Select(group => new TmdbListedGenre(group.Key, group.First().Name, movieIds.Contains(group.Key), showIds.Contains(group.Key)))
            .OrderBy(genre => genre.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<TmdbCertification?> GetCertificationAsync(TmdbMediaType type, int id, IReadOnlyList<string> regions, CancellationToken ct = default)
    {
        string cacheKey = $"tmdb:cert:{Segment(type)}:{id}";
        if (!cache.TryGet(cacheKey, out IReadOnlyDictionary<string, TmdbCertification>? byRegion) || byRegion is null)
        {
            byRegion = type == TmdbMediaType.Movie
                ? ReadMovieCertifications(await this.FetchAsync<ReleaseDatesResponse>($"movie/{id}/release_dates", [], ct))
                : ReadTvCertifications(await this.FetchAsync<ContentRatingsResponse>($"tv/{id}/content_ratings", [], ct));
            cache.Set(cacheKey, byRegion, CertificationLifetime);
        }

        foreach (string? region in regions)
        {
            if (!string.IsNullOrWhiteSpace(region) && byRegion.TryGetValue(region.ToUpperInvariant(), out TmdbCertification? certification))
            {
                return certification;
            }
        }

        return null;
    }

    private static Dictionary<string, TmdbCertification> ReadMovieCertifications(ReleaseDatesResponse response)
    {
        Dictionary<string, TmdbCertification> result = [];
        foreach (ReleaseDatesRegion region in (response.Results ?? []).Where(item => !string.IsNullOrWhiteSpace(item.Region)))
        {
            ReleaseDateEntry? entry = (region.ReleaseDates ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Certification))
                .OrderBy(item => item.Type == 3 ? 0 : 1)
                .FirstOrDefault();
            if (entry is not null)
            {
                string code = region.Region.ToUpperInvariant();
                result[code] = new TmdbCertification(code, entry.Certification.Trim(), Clean(entry.Descriptors));
            }
        }

        return result;
    }

    private static Dictionary<string, TmdbCertification> ReadTvCertifications(ContentRatingsResponse response)
    {
        Dictionary<string, TmdbCertification> result = [];
        foreach (ContentRatingEntry entry in (response.Results ?? []).Where(item => !string.IsNullOrWhiteSpace(item.Rating) && !string.IsNullOrWhiteSpace(item.Region)))
        {
            string code = entry.Region.ToUpperInvariant();
            result[code] = new TmdbCertification(code, entry.Rating.Trim(), Clean(entry.Descriptors));
        }

        return result;
    }

    private static string[] Clean(IReadOnlyList<string>? descriptors) =>
        [.. (descriptors ?? []).Where(item => !string.IsNullOrWhiteSpace(item))];

    private static IReadOnlyList<TmdbKeyword> ReadKeywords(IReadOnlyList<KeywordItem>? items) =>
        items is null ? [] : [.. items.Select(item => new TmdbKeyword(item.Id, item.Name ?? string.Empty))];

    private static string Segment(TmdbMediaType type) => type == TmdbMediaType.Movie ? "movie" : "tv";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void AddIfPresent(List<(string, string)> parameters, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters.Add((name, value));
        }
    }

    private static HttpRequestMessage CreateRequest(string path, IEnumerable<(string Name, string Value)> parameters, string key)
    {
        bool bearer = key.Contains('.', StringComparison.Ordinal);
        List<string> pairs = [.. parameters.Select(pair => $"{Uri.EscapeDataString(pair.Name)}={Uri.EscapeDataString(pair.Value)}")];
        pairs.Add("include_adult=false");
        if (!bearer)
        {
            pairs.Add($"api_key={Uri.EscapeDataString(key)}");
        }

        HttpRequestMessage request = new(HttpMethod.Get, $"{path}?{string.Join('&', pairs)}");
        if (bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return request;
    }

    private async Task<string> RequireKeyAsync(CancellationToken ct) =>
        await keys.GetApiKeyAsync(ct) is { Length: > 0 } key ? key : throw new TmdbNotConfiguredException();

    private async Task<T> GetAsync<T>(string path, IEnumerable<(string Name, string Value)> parameters, TimeSpan lifetime, CancellationToken ct)
        where T : class
    {
        List<(string Name, string Value)> ordered = [.. parameters.OrderBy(pair => pair.Name, StringComparer.Ordinal)];
        string cacheKey = $"tmdb:{path}?{string.Join('&', ordered.Select(pair => $"{Uri.EscapeDataString(pair.Name)}={Uri.EscapeDataString(pair.Value)}"))}";
        if (cache.TryGet(cacheKey, out T? cached) && cached is not null)
        {
            return cached;
        }

        T value = await this.FetchAsync<T>(path, ordered, ct);
        cache.Set(cacheKey, value, lifetime);
        return value;
    }

    private async Task<T> FetchAsync<T>(string path, IEnumerable<(string Name, string Value)> parameters, CancellationToken ct)
        where T : class
    {
        string key = await this.RequireKeyAsync(ct);
        using HttpRequestMessage request = CreateRequest(path, parameters, key);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new TmdbException($"TMDB answered {(int)response.StatusCode}.", response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new TmdbException("TMDB returned an empty response.");
    }

    private sealed record GenresResponse(
        [property: JsonPropertyName("genres")] IReadOnlyList<TmdbGenre>? Genres);

    private sealed record FindResponse(
        [property: JsonPropertyName("movie_results")] IReadOnlyList<FindResult>? MovieResults,
        [property: JsonPropertyName("tv_results")] IReadOnlyList<FindResult>? TvResults);

    private sealed record FindResult(
        [property: JsonPropertyName("id")] int Id);

    private sealed record ReleaseDatesResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<ReleaseDatesRegion>? Results);

    private sealed record ReleaseDatesRegion(
        [property: JsonPropertyName("iso_3166_1")] string Region,
        [property: JsonPropertyName("release_dates")] IReadOnlyList<ReleaseDateEntry>? ReleaseDates);

    private sealed record ReleaseDateEntry(
        [property: JsonPropertyName("certification")] string Certification,
        [property: JsonPropertyName("descriptors")] IReadOnlyList<string>? Descriptors,
        [property: JsonPropertyName("type")] int Type);

    private sealed record ContentRatingsResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<ContentRatingEntry>? Results);

    private sealed record ContentRatingEntry(
        [property: JsonPropertyName("iso_3166_1")] string Region,
        [property: JsonPropertyName("rating")] string Rating,
        [property: JsonPropertyName("descriptors")] IReadOnlyList<string>? Descriptors);

    private sealed record KeywordsResponse(
        [property: JsonPropertyName("keywords")] IReadOnlyList<KeywordItem>? Keywords,
        [property: JsonPropertyName("results")] IReadOnlyList<KeywordItem>? Results);

    private sealed record KeywordItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name);
}
