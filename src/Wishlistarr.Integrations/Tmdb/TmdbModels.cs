using System.Text.Json.Serialization;

namespace Wishlistarr.Integrations.Tmdb;

public enum TmdbMediaType
{
    Movie,
    Tv,
}

public sealed record TmdbPage<T>(
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("results")] IReadOnlyList<T> Results,
    [property: JsonPropertyName("total_pages")] int TotalPages,
    [property: JsonPropertyName("total_results")] int TotalResults);

public sealed record TmdbSearchResult(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("poster_path")] string? PosterPath,
    [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
    [property: JsonPropertyName("release_date")] string? ReleaseDate,
    [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
    [property: JsonPropertyName("vote_average")] double VoteAverage,
    [property: JsonPropertyName("genre_ids")] IReadOnlyList<int>? GenreIds = null,
    [property: JsonPropertyName("original_language")] string? OriginalLanguage = null);

public sealed record TmdbGenre(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record TmdbDetails(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("tagline")] string? Tagline,
    [property: JsonPropertyName("poster_path")] string? PosterPath,
    [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
    [property: JsonPropertyName("release_date")] string? ReleaseDate,
    [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
    [property: JsonPropertyName("runtime")] int? Runtime,
    [property: JsonPropertyName("number_of_seasons")] int? NumberOfSeasons,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("genres")] IReadOnlyList<TmdbGenre>? Genres,
    [property: JsonPropertyName("vote_average")] double VoteAverage,
    [property: JsonPropertyName("original_language")] string? OriginalLanguage = null);

public sealed record TmdbCastMember(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("character")] string? Character,
    [property: JsonPropertyName("profile_path")] string? ProfilePath);

public sealed record TmdbCrewMember(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("job")] string? Job,
    [property: JsonPropertyName("department")] string? Department);

public sealed record TmdbCredits(
    [property: JsonPropertyName("cast")] IReadOnlyList<TmdbCastMember> Cast,
    [property: JsonPropertyName("crew")] IReadOnlyList<TmdbCrewMember> Crew);

public sealed record TmdbVideo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("site")] string Site,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("official")] bool Official);

public sealed record TmdbVideos(
    [property: JsonPropertyName("results")] IReadOnlyList<TmdbVideo> Results);

public sealed record TmdbImage(
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("iso_639_1")] string? Language);

public sealed record TmdbImages(
    [property: JsonPropertyName("posters")] IReadOnlyList<TmdbImage> Posters,
    [property: JsonPropertyName("backdrops")] IReadOnlyList<TmdbImage> Backdrops,
    [property: JsonPropertyName("logos")] IReadOnlyList<TmdbImage> Logos);

public sealed record TmdbProvider(
    [property: JsonPropertyName("provider_id")] int ProviderId,
    [property: JsonPropertyName("provider_name")] string ProviderName,
    [property: JsonPropertyName("logo_path")] string? LogoPath);

public sealed record TmdbRegionProviders(
    [property: JsonPropertyName("link")] string? Link,
    [property: JsonPropertyName("flatrate")] IReadOnlyList<TmdbProvider>? Flatrate,
    [property: JsonPropertyName("rent")] IReadOnlyList<TmdbProvider>? Rent,
    [property: JsonPropertyName("buy")] IReadOnlyList<TmdbProvider>? Buy);

public sealed record TmdbWatchProviders(
    [property: JsonPropertyName("results")] IReadOnlyDictionary<string, TmdbRegionProviders> Results);

public sealed record TmdbCertification(string Region, string Rating, IReadOnlyList<string> Descriptors);

public sealed record TmdbDiscoverQuery(
    string? Genres = null,
    string? WithCertification = null,
    string? CertificationCountry = null,
    string? CertificationLessThanOrEqual = null,
    string? SortBy = null,
    int Page = 1);
