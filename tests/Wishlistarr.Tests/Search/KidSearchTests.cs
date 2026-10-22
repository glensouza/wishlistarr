using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wishlistarr.Api.Search;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Requests;
using Wishlistarr.Tests.Tmdb;

namespace Wishlistarr.Tests.Search;

public sealed class KidSearchTests
{
    private const string SearchPath = "/api/v1/kid/search";

    private static KidRequestApiFactory CreateFactory(FakeLibraryIndex? index = null)
    {
        KidRequestApiFactory factory = new(index);
        factory.Tmdb.SetBody("/3/search/multi", """
            {"page":1,"total_pages":3,"total_results":57,"results":[
              {"id":800,"media_type":"movie","title":"Tiny Tale","overview":"Gentle.","poster_path":"/tiny.jpg","release_date":"2019-05-01"},
              {"id":603,"media_type":"movie","title":"The Matrix","poster_path":"/matrix.jpg","release_date":"1999-03-31"},
              {"id":801,"media_type":"movie","title":"Night Terror","release_date":"2021-10-31"},
              {"id":802,"media_type":"movie","title":"Unrated Short","overview":"Secret plot.","poster_path":"/unrated.jpg","release_date":"2020-01-01"},
              {"id":803,"media_type":"movie","title":"Unreachable Rating","release_date":"2020-01-01"},
              {"id":82728,"media_type":"tv","name":"Bluey","poster_path":"/bluey.jpg","first_air_date":"2018-10-01"},
              {"id":5,"media_type":"person","name":"Some Actor"}]}
            """);
        SetMovie(factory, 800, "G");
        SetMovie(factory, 801, "R");
        factory.Tmdb.SetBody("/3/movie/802/release_dates", """{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"","type":3}]}]}""");
        return factory;
    }

    private static void SetMovie(KidRequestApiFactory factory, int id, string rating) =>
        factory.Tmdb.SetBody($"/3/movie/{id}/release_dates", $$"""{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"{{rating}}","type":3}]}]}""");

    private static async Task<JsonElement> GetAsync(HttpClient kid, string query, HttpStatusCode expected = HttpStatusCode.OK)
    {
        HttpResponseMessage response = await kid.GetAsync($"{SearchPath}?q={Uri.EscapeDataString(query)}");
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<int> Ids(JsonElement body) =>
        [.. body.GetProperty("results").EnumerateArray().Select(item => item.GetProperty("tmdbId").GetInt32())];

    [Fact]
    public async Task Disallowed_titles_never_appear_in_results_or_anywhere_in_the_body()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10, tvAgeLimit: 5);

        HttpResponseMessage response = await kid.GetAsync($"{SearchPath}?q=matrix");
        string text = await response.Content.ReadAsStringAsync();
        JsonElement body = JsonDocument.Parse(text).RootElement;

        Assert.Equal([800, 802, 82728], Ids(body));
        Assert.DoesNotContain("The Matrix", text);
        Assert.DoesNotContain("matrix.jpg", text);
        Assert.DoesNotContain("Night Terror", text);
        Assert.DoesNotContain("Some Actor", text);
        Assert.DoesNotContain("57", text);
    }

    [Fact]
    public async Task A_profile_without_limits_sees_everything_that_can_be_rated()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        Assert.Equal([800, 603, 801, 802, 82728], Ids(await GetAsync(kid, "matrix")));
    }

    [Fact]
    public async Task Genre_and_language_rules_remove_titles_the_rating_would_allow()
    {
        const string Page = """
            {"page":1,"total_pages":1,"total_results":2,"results":[
              {"id":800,"media_type":"movie","title":"Tiny Tale","poster_path":"/tiny.jpg","release_date":"2019-05-01","genre_ids":[16],"original_language":"ja"},
              {"id":603,"media_type":"movie","title":"The Matrix","poster_path":"/matrix.jpg","release_date":"1999-03-31","genre_ids":[28],"original_language":"en"}]}
            """;

        using KidRequestApiFactory genres = CreateFactory();
        genres.Tmdb.SetBody("/3/search/multi", Page);
        (HttpClient genreKid, _) = await genres.SignInKidAsync("Genres", movieAgeLimit: 16, blockedGenreIds: [16]);
        using KidRequestApiFactory languages = CreateFactory();
        languages.Tmdb.SetBody("/3/search/multi", Page);
        (HttpClient languageKid, _) = await languages.SignInKidAsync("Languages", movieAgeLimit: 16, allowedOriginalLanguages: ["en"]);
        using KidRequestApiFactory keywords = CreateFactory();
        keywords.Tmdb.SetBody("/3/search/multi", Page);
        (HttpClient keywordKid, _) = await keywords.SignInKidAsync("Keywords", movieAgeLimit: 16, blockedKeywordIds: [99]);

        Assert.Equal([603], Ids(await GetAsync(genreKid, "matrix")));
        Assert.Equal([603], Ids(await GetAsync(languageKid, "matrix")));
        Assert.Equal([800, 603], Ids(await GetAsync(keywordKid, "matrix")));
    }

    [Fact]
    public async Task Titles_whose_rating_cannot_be_read_are_left_out_while_unrated_ones_follow_the_policy_engine()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);

        List<int> ids = Ids(await GetAsync(kid, "matrix"));

        Assert.DoesNotContain(803, ids);
        Assert.Contains(802, ids);
    }

    [Fact]
    public async Task Results_come_from_the_signed_in_profiles_own_limits()
    {
        using KidRequestApiFactory strict = CreateFactory();
        (HttpClient young, _) = await strict.SignInKidAsync("Young", movieAgeLimit: 6);
        using KidRequestApiFactory loose = CreateFactory();
        (HttpClient older, _) = await loose.SignInKidAsync("Older", movieAgeLimit: 16);

        Assert.DoesNotContain(603, Ids(await GetAsync(young, "matrix")));
        List<int> olderIds = Ids(await GetAsync(older, "matrix"));
        Assert.Contains(603, olderIds);
        Assert.DoesNotContain(801, olderIds);
    }

    [Fact]
    public async Task Ratings_are_looked_up_once_per_title_and_served_from_the_cache_afterwards()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);

        await GetAsync(kid, "matrix");
        int searches = factory.Tmdb.Count("/3/search/multi");
        int ratings = factory.Tmdb.Requests.Count(request => request.Path.EndsWith("/release_dates", StringComparison.Ordinal)
            || request.Path.EndsWith("/content_ratings", StringComparison.Ordinal));
        await GetAsync(kid, "matrix");

        Assert.Equal(1, searches);
        Assert.Equal(6, ratings);
        Assert.Equal(1, factory.Tmdb.Count("/3/movie/800/release_dates"));
        Assert.Equal(searches, factory.Tmdb.Count("/3/search/multi"));
        Assert.Equal(2, factory.Tmdb.Count("/3/movie/803/release_dates"));
        Assert.Equal(
            ratings + 1,
            factory.Tmdb.Requests.Count(request => request.Path.EndsWith("/release_dates", StringComparison.Ordinal)
                || request.Path.EndsWith("/content_ratings", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Rating_lookups_run_in_parallel_within_the_documented_limit()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (_, int profileId) = await factory.SignInKidAsync(movieAgeLimit: 10);
        using IServiceScope scope = factory.Services.CreateScope();
        GateClient tmdb = new(count: 20);
        KidSearch search = new(scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>(), tmdb, [], TimeProvider.System, NullLogger<KidSearch>.Instance);

        KidSearchResponse? response = await search.SearchAsync(profileId, "many", 1);

        Assert.NotNull(response);
        Assert.Equal(20, response.Results.Count);
        Assert.InRange(tmdb.PeakConcurrency, 2, KidSearch.MaxParallelLookups);
    }

    [Fact]
    public async Task Lookups_that_outlive_the_budget_are_cut_off_and_dropped()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (_, int profileId) = await factory.SignInKidAsync();
        using IServiceScope scope = factory.Services.CreateScope();
        GateClient tmdb = new(count: 4) { Hang = true };
        ManualTimeProvider time = new();
        KidSearch search = new(scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>(), tmdb, [], time, NullLogger<KidSearch>.Instance);

        Task<KidSearchResponse?> pending = search.SearchAsync(profileId, "slow", 1);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));
        while (tmdb.Running < 4)
        {
            await Task.Delay(10, guard.Token);
        }

        Assert.False(pending.IsCompleted);
        time.Advance(KidSearch.LookupBudget);
        KidSearchResponse? response = await pending.WaitAsync(guard.Token);

        Assert.NotNull(response);
        Assert.Empty(response.Results);
        Assert.Equal(0, tmdb.Running);
    }

    [Fact]
    public async Task Unrated_titles_are_masked_in_the_raw_response_but_can_still_be_asked_for()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        factory.Tmdb.SetBody("/3/movie/802", """{"id":802,"title":"Unrated Short"}""");

        string text = await (await kid.GetAsync($"{SearchPath}?q=matrix")).Content.ReadAsStringAsync();
        JsonElement results = JsonDocument.Parse(text).RootElement.GetProperty("results");
        JsonElement masked = results.EnumerateArray().Single(item => item.GetProperty("tmdbId").GetInt32() == 802);
        JsonElement allowed = results[0];

        Assert.DoesNotContain("Secret plot.", text);
        Assert.DoesNotContain("unrated.jpg", text);
        Assert.True(masked.GetProperty("needsParent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("posterUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("overview").ValueKind);
        Assert.Equal("Unrated Short", masked.GetProperty("title").GetString());
        Assert.Equal("2020", masked.GetProperty("year").GetString());
        Assert.False(allowed.GetProperty("needsParent").GetBoolean());
        Assert.Equal("Gentle.", allowed.GetProperty("overview").GetString());
        Assert.Equal(
            HttpStatusCode.Created,
            (await KidRequestApiFactory.RequestAsync(kid, "movie", 802)).StatusCode);
    }

    [Fact]
    public async Task Shows_are_filtered_by_the_profiles_tv_age_limit()
    {
        using KidRequestApiFactory factory = CreateFactory();
        factory.Tmdb.SetBody("/3/search/multi", """
            {"page":1,"total_pages":1,"total_results":2,"results":[
              {"id":82728,"media_type":"tv","name":"Bluey","first_air_date":"2018-10-01"},
              {"id":810,"media_type":"tv","name":"Grown Up Show","first_air_date":"2015-01-01"}]}
            """);
        factory.Tmdb.SetBody("/3/tv/810/content_ratings", """{"results":[{"iso_3166_1":"US","rating":"TV-MA"}]}""");
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 16, tvAgeLimit: 7);

        string text = await (await kid.GetAsync($"{SearchPath}?q=show")).Content.ReadAsStringAsync();

        Assert.Equal([82728], Ids(JsonDocument.Parse(text).RootElement));
        Assert.DoesNotContain("Grown Up Show", text);
    }

    [Fact]
    public async Task Search_is_unavailable_when_tmdb_has_no_key()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        ((FixedKeyProvider)factory.Services.GetRequiredService<ITmdbApiKeyProvider>()).Key = null;

        HttpResponseMessage response = await kid.GetAsync($"{SearchPath}?q=matrix");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Search isn't working right now.", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Tmdb.Count("/3/search/multi"));
    }

    [Fact]
    public async Task An_archived_profile_gets_nothing_and_tmdb_is_not_called()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (_, int profileId) = await factory.SignInKidAsync();
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        KidProfile profile = await db.KidProfiles.SingleAsync(row => row.Id == profileId);
        profile.ArchivedAt = factory.Clock.GetUtcNow();
        await db.SaveChangesAsync();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<KidSearch>().SearchAsync(profileId, "matrix", 1));
        Assert.Equal(0, factory.Tmdb.Count("/3/search/multi"));
    }

    [Theory]
    [InlineData("w185")]
    [InlineData("w500")]
    [InlineData("original")]
    public async Task Only_the_search_poster_size_is_served_to_kids(string size)
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        HttpResponseMessage image = await kid.GetAsync($"/api/v1/kid/images/tmdb/{size}/tiny.jpg");

        Assert.Equal(HttpStatusCode.BadRequest, image.StatusCode);
        Assert.DoesNotContain(factory.Tmdb.Requests, request => request.Path.StartsWith("/t/p/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Availability_is_shown_when_the_library_index_is_ready()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 800, new MediaItem("1", "plex://movie/a", "Tiny Tale", 2019, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = CreateFactory(index);
        (HttpClient kid, _) = await factory.SignInKidAsync();

        JsonElement body = await GetAsync(kid, "matrix");

        Dictionary<int, string?> byId = body.GetProperty("results").EnumerateArray()
            .ToDictionary(item => item.GetProperty("tmdbId").GetInt32(), item => item.GetProperty("availability").GetString());
        Assert.Equal("inLibrary", byId[800]);
        Assert.Equal("notInLibrary", byId[603]);
    }

    [Fact]
    public async Task Availability_is_absent_without_an_index_or_before_it_can_answer()
    {
        using KidRequestApiFactory without = CreateFactory();
        (HttpClient kid, _) = await without.SignInKidAsync();
        Assert.All(
            (await GetAsync(kid, "matrix")).GetProperty("results").EnumerateArray(),
            item => Assert.Equal(JsonValueKind.Null, item.GetProperty("availability").ValueKind));

        using KidRequestApiFactory notReady = CreateFactory(new FakeLibraryIndex { Ready = false });
        (HttpClient other, _) = await notReady.SignInKidAsync();
        Assert.All(
            (await GetAsync(other, "matrix")).GetProperty("results").EnumerateArray(),
            item => Assert.Equal(JsonValueKind.Null, item.GetProperty("availability").ValueKind));
        Assert.Equal(0, notReady.LibraryScans);
    }

    [Fact]
    public async Task A_result_can_be_requested_through_the_kid_request_endpoint()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        factory.Tmdb.SetBody("/3/movie/800", """{"id":800,"title":"Tiny Tale"}""");
        JsonElement first = (await GetAsync(kid, "tiny")).GetProperty("results")[0];

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(
            kid,
            first.GetProperty("kind").GetString()!,
            first.GetProperty("tmdbId").GetInt32());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Tiny Tale", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Paging_reports_whether_more_exists_without_totals()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        JsonElement first = await GetAsync(kid, "matrix");
        HttpResponseMessage last = await kid.GetAsync($"{SearchPath}?q=matrix&page=3");
        JsonElement third = await last.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, first.GetProperty("page").GetInt32());
        Assert.Equal("3", factory.Tmdb.Requests.Last(request => request.Path == "/3/search/multi").Query["page"]);
        Assert.False(third.GetProperty("hasMore").GetBoolean());
        Assert.Equal(["query", "page", "hasMore", "results"], first.EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" a ")]
    [InlineData("a\u0007b")]
    public async Task Short_or_malformed_queries_are_rejected(string query)
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        await GetAsync(kid, query, HttpStatusCode.BadRequest);

        Assert.Equal(0, factory.Tmdb.Count("/3/search/multi"));
    }

    [Fact]
    public async Task Long_queries_and_out_of_range_pages_are_rejected()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        await GetAsync(kid, new string('a', KidSearch.MaxQueryLength + 1), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.BadRequest, (await kid.GetAsync($"{SearchPath}?q=matrix&page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await kid.GetAsync($"{SearchPath}?q=matrix&page={KidSearch.MaxPage + 1}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await kid.GetAsync($"{SearchPath}?q={new string('a', KidSearch.MaxQueryLength)}")).StatusCode);
        Assert.Equal(1, factory.Tmdb.Count("/3/search/multi"));
    }

    [Fact]
    public async Task Whitespace_in_queries_is_collapsed_before_it_reaches_tmdb()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        JsonElement body = await GetAsync(kid, "  the   matrix ");

        Assert.Equal("the matrix", body.GetProperty("query").GetString());
        Assert.Equal("the matrix", factory.Tmdb.Requests.Last(request => request.Path == "/3/search/multi").Query["query"]);
    }

    [Fact]
    public async Task Only_a_signed_in_kid_can_search()
    {
        using KidRequestApiFactory factory = CreateFactory();
        HttpClient anonymous = factory.CreateClient();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{SearchPath}?q=matrix")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await parent.GetAsync($"{SearchPath}?q=matrix")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/kid/images/tmdb/w342/matrix.jpg")).StatusCode);
        Assert.Equal(0, factory.Tmdb.Count("/3/search/multi"));
    }

    [Fact]
    public async Task Search_failures_are_kid_safe()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        factory.Tmdb.Enqueue("/3/search/multi", 500);

        HttpResponseMessage response = await kid.GetAsync($"{SearchPath}?q=matrix");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Search isn't working right now.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Posters_are_served_to_a_signed_in_kid_and_responses_are_not_cached()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        HttpResponseMessage search = await kid.GetAsync($"{SearchPath}?q=matrix");
        JsonElement body = await search.Content.ReadFromJsonAsync<JsonElement>();
        string poster = body.GetProperty("results")[0].GetProperty("posterUrl").GetString()!;
        HttpResponseMessage image = await kid.GetAsync(poster);

        Assert.Equal("/api/v1/kid/images/tmdb/w342/tiny.jpg", poster);
        Assert.True(search.Headers.CacheControl is { NoStore: true, Private: true });
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(FakeTmdbServer.ImageBytes, await image.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await kid.GetAsync("/api/v1/kid/images/tmdb/w1/x.jpg")).StatusCode);
    }

    private sealed class GateClient(int count) : ITmdbClient
    {
        private int running;
        private int peak;

        public bool Hang { get; init; }

        public int PeakConcurrency => this.peak;

        public int Running => Volatile.Read(ref this.running);

        public Task<TmdbPage<TmdbSearchResult>> SearchAsync(string query, TmdbMediaType? type = null, int page = 1, string language = "en-US", CancellationToken ct = default) =>
            Task.FromResult(new TmdbPage<TmdbSearchResult>(
                1,
                [.. Enumerable.Range(1, count).Select(id => new TmdbSearchResult(id, "movie", $"Movie {id}", null, null, null, null, null, null, 0))],
                1,
                count));

        public async Task<TmdbCertification?> GetCertificationAsync(TmdbMediaType type, int id, IReadOnlyList<string> regions, CancellationToken ct = default)
        {
            int now = Interlocked.Increment(ref this.running);
            int seen;
            while (now > (seen = Volatile.Read(ref this.peak)) && Interlocked.CompareExchange(ref this.peak, now, seen) != seen)
            {
            }

            try
            {
                if (this.Hang)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                await Task.Delay(50, ct);
                return new TmdbCertification("US", "G", []);
            }
            finally
            {
                Interlocked.Decrement(ref this.running);
            }
        }

        public Task<bool> TestApiKeyAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbDetails> GetDetailsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbCredits> GetCreditsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbVideos> GetVideosAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbImages> GetImagesAsync(TmdbMediaType type, int id, string imageLanguages = "en,null", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbPage<TmdbSearchResult>> DiscoverAsync(TmdbMediaType type, TmdbDiscoverQuery query, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbPage<TmdbSearchResult>> GetTrendingAsync(TmdbMediaType type, string window = "week", int page = 1, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbWatchProviders> GetWatchProvidersAsync(TmdbMediaType type, int id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int?> FindIdAsync(TmdbMediaType type, ExternalIdSource source, string externalId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => this.now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(callback, state, this.now + dueTime);
            lock (this.timers)
            {
                this.timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (this.timers)
            {
                this.now += by;
                due = [.. this.timers.Where(timer => timer.Due <= this.now)];
            }

            foreach (ManualTimer timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            private bool done;

            public DateTimeOffset Due => due;

            public void Fire()
            {
                if (!this.done)
                {
                    this.done = true;
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !this.done;

            public void Dispose() => this.done = true;

            public ValueTask DisposeAsync()
            {
                this.done = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
