using System.Net;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Tests.Tmdb;

public sealed class TmdbClientTests
{
    [Fact]
    public async Task Every_request_sends_include_adult_false()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        ITmdbClient client = harness.Client;

        await client.SearchAsync("matrix");
        await client.SearchAsync("matrix", TmdbMediaType.Movie);
        await client.DiscoverAsync(TmdbMediaType.Movie, new TmdbDiscoverQuery(Genres: "28"));
        await client.GetTrendingAsync(TmdbMediaType.Movie);
        await client.GetDetailsAsync(TmdbMediaType.Movie, 603);
        await client.GetCertificationAsync(TmdbMediaType.Movie, 603, ["US"]);

        Assert.Equal(6, harness.Server.Requests.Count);
        Assert.All(harness.Server.Requests, request => Assert.Equal("false", request.Query["include_adult"]));
    }

    [Fact]
    public async Task Adult_content_cannot_be_requested_through_the_discover_query()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        await harness.Client.DiscoverAsync(TmdbMediaType.Tv, new TmdbDiscoverQuery(SortBy: "popularity.desc", CertificationCountry: "US", CertificationLessThanOrEqual: "TV-PG"));

        FakeTmdbServer.RecordedRequest request = Assert.Single(harness.Server.Requests);
        Assert.Equal("false", request.Query["include_adult"]);
        Assert.Equal("TV-PG", request.Query["certification.lte"]);
        Assert.Equal("US", request.Query["certification_country"]);
    }

    [Fact]
    public async Task Search_parses_results()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        TmdbPage<TmdbSearchResult> page = await harness.Client.SearchAsync("matrix");

        TmdbSearchResult result = Assert.Single(page.Results);
        Assert.Equal(603, result.Id);
        Assert.Equal("The Matrix", result.Title);
        Assert.Equal("matrix", harness.Server.Requests[0].Query["query"]);
    }

    [Fact]
    public async Task Details_credits_videos_images_and_providers_are_typed()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        ITmdbClient client = harness.Client;

        TmdbDetails details = await client.GetDetailsAsync(TmdbMediaType.Movie, 603);
        TmdbCredits credits = await client.GetCreditsAsync(TmdbMediaType.Movie, 603);
        TmdbVideos videos = await client.GetVideosAsync(TmdbMediaType.Movie, 603);
        TmdbImages images = await client.GetImagesAsync(TmdbMediaType.Movie, 603);
        TmdbWatchProviders providers = await client.GetWatchProvidersAsync(TmdbMediaType.Movie, 603);

        Assert.Equal(136, details.Runtime);
        Assert.Equal("Action", Assert.Single(details.Genres!).Name);
        Assert.Equal("Neo", Assert.Single(credits.Cast).Character);
        Assert.Equal("Director", Assert.Single(credits.Crew).Job);
        Assert.Equal("YouTube", Assert.Single(videos.Results).Site);
        Assert.Equal("/p.jpg", Assert.Single(images.Posters).FilePath);
        Assert.Equal("Stream", Assert.Single(providers.Results["US"].Flatrate!).ProviderName);
    }

    [Fact]
    public async Task Responses_are_cached_and_keyed_by_parameters()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        ITmdbClient client = harness.Client;

        await client.SearchAsync("matrix");
        await client.SearchAsync("matrix");
        await client.GetDetailsAsync(TmdbMediaType.Movie, 603);
        await client.GetDetailsAsync(TmdbMediaType.Movie, 603);

        Assert.Equal(1, harness.Server.Count("/3/search/multi"));
        Assert.Equal(1, harness.Server.Count("/3/movie/603"));

        await client.SearchAsync("other");
        await client.GetDetailsAsync(TmdbMediaType.Movie, 603, "fr-FR");

        Assert.Equal(2, harness.Server.Count("/3/search/multi"));
        Assert.Equal(2, harness.Server.Count("/3/movie/603"));
    }

    [Fact]
    public async Task Failed_responses_are_not_cached()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        harness.Server.Enqueue("/3/movie/603", 500);

        TmdbException error = await Assert.ThrowsAsync<TmdbException>(() => harness.Client.GetDetailsAsync(TmdbMediaType.Movie, 603));
        TmdbDetails details = await harness.Client.GetDetailsAsync(TmdbMediaType.Movie, 603);

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.Equal(603, details.Id);
    }

    [Fact]
    public async Task Movie_certification_prefers_theatrical_and_falls_back_through_regions()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        TmdbCertification? us = await harness.Client.GetCertificationAsync(TmdbMediaType.Movie, 603, ["US", "GB"]);
        TmdbCertification? gb = await harness.Client.GetCertificationAsync(TmdbMediaType.Movie, 603, ["DE", "GB", "US"]);
        TmdbCertification? skipsEmpty = await harness.Client.GetCertificationAsync(TmdbMediaType.Movie, 603, ["FR", "US"]);
        TmdbCertification? none = await harness.Client.GetCertificationAsync(TmdbMediaType.Movie, 603, ["DE", "JP"]);

        Assert.Equal("PG-13", us?.Rating);
        Assert.Equal("US", us?.Region);
        Assert.Equal(["Violence"], us?.Descriptors);
        Assert.Equal("15", gb?.Rating);
        Assert.Equal("US", skipsEmpty?.Region);
        Assert.Null(none);
    }

    [Fact]
    public async Task Tv_certification_uses_content_ratings_with_descriptors()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        TmdbCertification? us = await harness.Client.GetCertificationAsync(TmdbMediaType.Tv, 1399, ["us"]);
        TmdbCertification? fallback = await harness.Client.GetCertificationAsync(TmdbMediaType.Tv, 1399, ["GB", "DE"]);

        Assert.Equal("TV-MA", us?.Rating);
        Assert.Equal(["Language", "Violence"], us?.Descriptors);
        Assert.Equal("DE", fallback?.Region);
        Assert.Equal("16", fallback?.Rating);
    }

    [Fact]
    public async Task Certifications_are_cached_per_title_across_region_orders()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        await harness.Client.GetCertificationAsync(TmdbMediaType.Movie, 603, ["US"]);
        await harness.Client.GetCertificationAsync(TmdbMediaType.Movie, 603, ["GB"]);
        await harness.Client.GetCertificationAsync(TmdbMediaType.Tv, 1399, ["US"]);
        await harness.Client.GetCertificationAsync(TmdbMediaType.Tv, 1399, ["DE"]);

        Assert.Equal(1, harness.Server.Count("/3/movie/603/release_dates"));
        Assert.Equal(1, harness.Server.Count("/3/tv/1399/content_ratings"));
    }

    [Fact]
    public async Task Too_many_requests_is_retried_honouring_retry_after()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        harness.Server.Enqueue("/3/movie/603", 429, "7");
        harness.Server.Enqueue("/3/movie/603", 429, "3");

        TmdbDetails details = await harness.Client.GetDetailsAsync(TmdbMediaType.Movie, 603);

        Assert.Equal(603, details.Id);
        Assert.Equal(3, harness.Server.Count("/3/movie/603"));
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(3)], harness.Time.Delays);
    }

    [Fact]
    public async Task Too_many_requests_without_retry_after_backs_off_exponentially()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        harness.Server.Enqueue("/3/movie/603", 429);
        harness.Server.Enqueue("/3/movie/603", 429);
        harness.Server.Enqueue("/3/movie/603", 429);

        await harness.Client.GetDetailsAsync(TmdbMediaType.Movie, 603);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], harness.Time.Delays);
    }

    [Fact]
    public async Task Retry_after_is_capped_and_total_wait_is_bounded()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync(options => options.MaxRetries = 2);
        for (int index = 0; index < 5; index++)
        {
            harness.Server.Enqueue("/3/movie/603", 429, "3600");
        }

        TmdbException error = await Assert.ThrowsAsync<TmdbException>(() => harness.Client.GetDetailsAsync(TmdbMediaType.Movie, 603));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(2, harness.Server.Count("/3/movie/603"));
        Assert.Equal([TimeSpan.FromSeconds(30)], harness.Time.Delays);
    }

    [Fact]
    public async Task Missing_key_is_reported_before_any_request()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        harness.Keys.Key = null;

        await Assert.ThrowsAsync<TmdbNotConfiguredException>(() => harness.Client.SearchAsync("matrix"));
        await Assert.ThrowsAsync<TmdbNotConfiguredException>(() => harness.Client.TestApiKeyAsync());

        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task Api_key_test_accepts_a_valid_key_and_rejects_an_invalid_one()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        Assert.True(await harness.Client.TestApiKeyAsync());

        harness.Keys.Key = "wrong";
        Assert.False(await harness.Client.TestApiKeyAsync());
    }

    [Fact]
    public async Task Read_access_tokens_are_sent_as_bearer_credentials()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        harness.Keys.Key = $"{FakeTmdbServer.ValidApiKey}.token";

        Assert.True(await harness.Client.TestApiKeyAsync());

        FakeTmdbServer.RecordedRequest request = Assert.Single(harness.Server.Requests);
        Assert.Equal($"Bearer {FakeTmdbServer.ValidApiKey}.token", request.Authorization);
        Assert.False(request.Query.ContainsKey("api_key"));
    }

    [Fact]
    public async Task Retries_are_bounded_by_count()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync(options => options.MaxRetries = 2);
        for (int index = 0; index < 5; index++)
        {
            harness.Server.Enqueue("/3/movie/603", 429, "1");
        }

        TmdbException error = await Assert.ThrowsAsync<TmdbException>(() => harness.Client.GetDetailsAsync(TmdbMediaType.Movie, 603));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(3, harness.Server.Count("/3/movie/603"));
    }

    [Fact]
    public async Task Retry_after_accepts_an_http_date()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        harness.Server.Enqueue("/3/movie/603", 429, DateTimeOffset.UtcNow.AddSeconds(10).ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        await harness.Client.GetDetailsAsync(TmdbMediaType.Movie, 603);

        TimeSpan delay = Assert.Single(harness.Time.Delays);
        Assert.InRange(delay, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cache_stays_within_its_entry_limit()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync(options => options.CacheMaxEntries = 5);

        for (int index = 0; index < 30; index++)
        {
            await harness.Client.SearchAsync($"query {index}");
        }

        Assert.InRange(harness.Cache.EntryCount, 1, 5);
    }

    [Fact]
    public async Task Query_values_with_separators_are_sent_intact()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();

        await harness.Client.SearchAsync("a&page=2=b");
        await harness.Client.SearchAsync("a");

        Assert.Equal(2, harness.Server.Count("/3/search/multi"));
        Assert.Equal("a&page=2=b", harness.Server.Requests[0].Query["query"]);
    }

    [Fact]
    public async Task Include_adult_is_false_on_every_endpoint()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        ITmdbClient client = harness.Client;

        await client.GetCreditsAsync(TmdbMediaType.Movie, 603);
        await client.GetVideosAsync(TmdbMediaType.Movie, 603);
        await client.GetImagesAsync(TmdbMediaType.Movie, 603);
        await client.GetWatchProvidersAsync(TmdbMediaType.Movie, 603);
        await client.GetCertificationAsync(TmdbMediaType.Tv, 1399, ["US"]);
        await client.TestApiKeyAsync();

        Assert.Equal(6, harness.Server.Requests.Count);
        Assert.All(harness.Server.Requests, request => Assert.Equal("false", request.Query["include_adult"]));
    }

    [Fact]
    public async Task Certification_lookup_ignores_blank_regions_and_entries_without_a_region()
    {
        await using TmdbHarness harness = await TmdbHarness.CreateAsync();
        harness.Server.SetBody("/3/movie/603/release_dates", """{"results":[{"release_dates":[{"certification":"R","type":3}]},{"iso_3166_1":"US","release_dates":[{"certification":"PG","type":3}]}]}""");
        harness.Server.SetBody("/3/tv/1399/content_ratings", """{"results":[{"rating":"TV-14"},{"iso_3166_1":"US","rating":"TV-G"}]}""");

        TmdbCertification? movie = await harness.Client.GetCertificationAsync(TmdbMediaType.Movie, 603, [null!, "", "US"]);
        TmdbCertification? tv = await harness.Client.GetCertificationAsync(TmdbMediaType.Tv, 1399, [null!, "US"]);

        Assert.Equal("PG", movie?.Rating);
        Assert.Equal("TV-G", tv?.Rating);
    }
}
