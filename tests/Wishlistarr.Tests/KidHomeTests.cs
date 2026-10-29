using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Lists;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Requests;
using Wishlistarr.Tests.Tmdb;

namespace Wishlistarr.Tests;

public sealed class KidHomeTests
{
    private const string HomePath = "/api/v1/kid/home";

    private const string PolicyMovies = """
        {"page":1,"results":[
          {"id":800,"title":"Tiny Tale","overview":"Gentle.","poster_path":"/tiny.jpg","release_date":"2019-05-01","genre_ids":[16,10751],"original_language":"en"},
          {"id":806,"title":"Unreachable Rating","overview":"Cannot read.","poster_path":"/gone.jpg","release_date":"2020-01-01","genre_ids":[16],"original_language":"en"},
          {"id":603,"title":"The Matrix","overview":"A hacker learns the truth.","poster_path":"/matrix.jpg","release_date":"1999-03-31","genre_ids":[28],"original_language":"en"},
          {"id":801,"title":"Night Terror","overview":"Too old.","poster_path":"/terror.jpg","release_date":"2021-10-31","genre_ids":[27],"original_language":"en"},
          {"id":802,"title":"Unrated Short","overview":"Secret plot","poster_path":"/unrated.jpg","release_date":"2020-01-01","genre_ids":[35],"original_language":"en"},
          {"id":804,"title":"Quiet Japanese","overview":"A quiet story.","release_date":"2018-04-01","genre_ids":[16],"original_language":"ja"}
        ],"total_pages":1,"total_results":6}
        """;

    private const string PolicyShows = """
        {"page":1,"results":[
          {"id":82728,"name":"Bluey","overview":"A puppy.","poster_path":"/bluey.jpg","first_air_date":"2018-10-01","genre_ids":[16,10762],"original_language":"en"},
          {"id":810,"name":"Grown Up Show","overview":"Late night.","poster_path":"/grown.jpg","first_air_date":"2019-01-01","genre_ids":[18],"original_language":"en"}
        ],"total_pages":1,"total_results":2}
        """;

    private static KidRequestApiFactory PolicyFactory(FakeLibraryIndex? index = null)
    {
        KidRequestApiFactory factory = new(index);
        factory.Tmdb.SetCatalog("/3/discover/movie", PolicyMovies);
        factory.Tmdb.SetCatalog("/3/discover/tv", PolicyShows);
        SetMovie(factory, 800, "G");
        SetMovie(factory, 801, "R");
        SetMovie(factory, 804, "G");
        factory.Tmdb.SetBody("/3/movie/802/release_dates", """{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"","type":3}]}]}""");
        factory.Tmdb.Enqueue("/3/movie/806/release_dates", 500);
        factory.Tmdb.SetBody("/3/tv/810/content_ratings", """{"results":[{"iso_3166_1":"US","rating":"TV-MA"}]}""");
        return factory;
    }

    private static void SetMovie(KidRequestApiFactory factory, int id, string rating) =>
        factory.Tmdb.SetBody($"/3/movie/{id}/release_dates", $$"""{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"{{rating}}","type":3}]}]}""");

    private static List<string> RowIds(JsonElement body) =>
        [.. body.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetString()!)];

    [Fact]
    public async Task Discovery_settings_validate_input_and_do_not_return_secrets()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpResponseMessage invalid = await parent.PutAsJsonAsync("/api/v1/settings/discovery-lists", new
        {
            lists = new[] { new { provider = "other", listId = 42, name = "Picks" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        HttpResponseMessage saved = await parent.PutAsJsonAsync("/api/v1/settings/discovery-lists", new
        {
            lists = Array.Empty<object>(),
            traktKey = "demo-key",
        });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        string result = await parent.GetStringAsync("/api/v1/settings/discovery-lists");
        Assert.DoesNotContain("demo-key", result);
        Assert.True(JsonDocument.Parse(result).RootElement.GetProperty("hasTraktKey").GetBoolean());
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        Assert.DoesNotContain("demo-key", (await db.Settings.SingleAsync(setting => setting.Key == "system.discovery-lists.trakt-key")).Value);
    }

    [Fact]
    public async Task Refreshed_list_rows_only_show_titles_allowed_by_the_profile()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        factory.Tmdb.SetBody("/3/movie/800", """{"id":800,"title":"Tiny Tale","genres":[{"id":16}],"original_language":"en"}""");
        factory.Tmdb.SetBody("/3/movie/801", """{"id":801,"title":"Night Terror","genres":[{"id":27}],"original_language":"en"}""");
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 6, homeRows: []);
        using IServiceScope scope = factory.Services.CreateScope();
        ListHttpFactory provider = new();
        DiscoveryLists lists = new(scope.ServiceProvider.GetRequiredService<SettingsStore>(), provider, factory.Services.GetRequiredService<DiscoveryListGate>(), factory.Clock);
        DiscoveryList[] sources = [new("trakt", 42, "Family picks"), new("mdblist-external", 43, "Imported picks")];
        Assert.True(await lists.SaveAsync(new DiscoveryListSettings(sources, "test-key", "test-key"), default));
        await lists.RunAsync(default);

        JsonElement home = await kid.GetFromJsonAsync<JsonElement>(HomePath);
        JsonElement row = home.GetProperty("rows")[0];
        Assert.Equal("Family picks", row.GetProperty("name").GetString());
        Assert.Equal([800], row.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tmdbId").GetInt32()));
        JsonElement imported = home.GetProperty("rows")[1];
        Assert.Equal("Imported picks", imported.GetProperty("name").GetString());
        Assert.Equal([800], imported.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tmdbId").GetInt32()));
        Assert.Equal(TimeSpan.FromHours(6), factory.Services.GetRequiredService<JobRunner>().Find(DiscoveryLists.JobName)!.DefaultInterval);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PutAsJsonAsync("/api/v1/settings/discovery-lists", new { lists = Array.Empty<object>() })).StatusCode);

        provider.Body = """[{"type":"movie","movie":{"ids":{"tmdb":801}}}]""";
        provider.MdbBody = """{"movies":[{"id":801,"rank":1}],"shows":[]}""";
        await lists.RunAsync(default);
        JsonElement refreshed = await kid.GetFromJsonAsync<JsonElement>(HomePath);
        Assert.Empty(refreshed.GetProperty("rows").EnumerateArray());
        provider.Status = HttpStatusCode.ServiceUnavailable;
        Assert.True(await lists.SaveAsync(new DiscoveryListSettings(sources, null, null), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lists.RunAsync(default));
        Assert.Equal(801, (await lists.SnapshotsAsync(default))[0].Items[0].TmdbId);
    }

    private sealed class ListHttpFactory : IHttpClientFactory
    {
        public string Body { get; set; } = """[{"type":"movie","movie":{"ids":{"tmdb":800}}},{"type":"movie","movie":{"ids":{"tmdb":801}}}]""";
        public string MdbBody { get; set; } = """{"movies":[{"id":800,"rank":1},{"id":801,"rank":2}],"shows":[]}""";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(new ListHandler(this));

        private sealed class ListHandler(ListHttpFactory source) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(
                new HttpResponseMessage(source.Status) { Content = new StringContent(request.RequestUri!.Host == "api.mdblist.com" ? source.MdbBody : source.Body) });
        }
    }

    [Fact]
    public async Task Renaming_during_a_refresh_keeps_the_previous_snapshot_and_discards_the_stale_result()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        using IServiceScope first = factory.Services.CreateScope();
        using IServiceScope second = factory.Services.CreateScope();
        DiscoveryListGate gate = factory.Services.GetRequiredService<DiscoveryListGate>();
        ListHttpFactory initial = new();
        DiscoveryLists lists = new(first.ServiceProvider.GetRequiredService<SettingsStore>(), initial, gate, factory.Clock);
        DiscoveryList old = new("trakt", 42, "Old name");
        Assert.True(await lists.SaveAsync(new DiscoveryListSettings([old], "test-key", null), default));
        await lists.RunAsync(default);
        BlockingListFactory blocked = new();
        DiscoveryLists running = new(first.ServiceProvider.GetRequiredService<SettingsStore>(), blocked, gate, factory.Clock);
        DiscoveryLists editor = new(second.ServiceProvider.GetRequiredService<SettingsStore>(), initial, gate, factory.Clock);
        Task refresh = running.RunAsync(default);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        DiscoveryList renamed = old with { Name = "New name" };
        Assert.True(await editor.SaveAsync(new DiscoveryListSettings([renamed], null, null), default));
        blocked.Release.TrySetResult();
        await refresh;
        DiscoveryListSnapshot snapshot = Assert.Single(await editor.SnapshotsAsync(default));
        Assert.Equal("New name", snapshot.Source.Name);
        Assert.Equal(800, snapshot.Items[0].TmdbId);
        initial.Status = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.RunAsync(default));
        snapshot = Assert.Single(await editor.SnapshotsAsync(default));
        Assert.Equal("New name", snapshot.Source.Name);
        Assert.Equal(800, snapshot.Items[0].TmdbId);
    }

    private sealed class BlockingListFactory : IHttpClientFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(BlockingListFactory source) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                source.Started.TrySetResult();
                await source.Release.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"type":"movie","movie":{"ids":{"tmdb":999}}}]""") };
            }
        }
    }

    [Fact]
    public async Task A_late_refresh_cannot_replace_a_newer_refresh()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        using IServiceScope first = factory.Services.CreateScope();
        using IServiceScope second = factory.Services.CreateScope();
        DiscoveryListGate gate = factory.Services.GetRequiredService<DiscoveryListGate>();
        BlockingListFactory blocked = new();
        DiscoveryLists slow = new(first.ServiceProvider.GetRequiredService<SettingsStore>(), blocked, gate, factory.Clock);
        DiscoveryLists fast = new(second.ServiceProvider.GetRequiredService<SettingsStore>(), new ListHttpFactory(), gate, factory.Clock);
        Assert.True(await slow.SaveAsync(new DiscoveryListSettings([new("trakt", 42, "Picks")], "test-key", null), default));
        Task older = slow.RunAsync(default);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fast.RunAsync(default);
        blocked.Release.TrySetResult();
        await older;
        Assert.Equal(800, Assert.Single(await fast.SnapshotsAsync(default)).Items[0].TmdbId);
    }

    [Fact]
    public async Task Rate_limit_cools_both_mdblist_namespaces_until_retry_after()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        using IServiceScope scope = factory.Services.CreateScope();
        RateLimitedListFactory provider = new();
        DiscoveryLists lists = new(scope.ServiceProvider.GetRequiredService<SettingsStore>(), provider,
            factory.Services.GetRequiredService<DiscoveryListGate>(), factory.Clock);
        DiscoveryList[] sources = [new("mdblist", 42, "One"), new("mdblist-external", 43, "Two")];
        Assert.True(await lists.SaveAsync(new DiscoveryListSettings(sources, null, "test-key"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lists.RunAsync(default));
        Assert.Equal(1, provider.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lists.RunAsync(default));
        Assert.Equal(1, provider.Calls);
        factory.Clock.Advance(TimeSpan.FromSeconds(120));
        await lists.RunAsync(default);
        Assert.Equal(3, provider.Calls);
        Assert.Equal(2, (await lists.SnapshotsAsync(default)).Count);
    }

    private sealed class RateLimitedListFactory : IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(RateLimitedListFactory source) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                source.Calls++;
                if (source.Calls == 1)
                {
                    HttpResponseMessage limited = new(HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                    return Task.FromResult(limited);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"movies":[],"shows":[]}""") });
            }
        }
    }

    [Fact]
    public async Task Rows_only_contain_titles_the_profile_policy_allows()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 6, tvAgeLimit: 7);

        HttpResponseMessage response = await kid.GetAsync(HomePath);
        string text = await response.Content.ReadAsStringAsync();
        JsonElement body = JsonDocument.Parse(text).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl is { NoStore: true, Private: true });
        Assert.Equal(["popular", "genre:16", "genre:10751", "genre:35"], RowIds(body));
        Assert.Contains("Tiny Tale", text);
        Assert.Contains("Bluey", text);
        Assert.Contains("/api/v1/kid/images/tmdb/w342/tiny.jpg", text);
        Assert.Contains("/api/v1/kid/images/tmdb/w342/bluey.jpg", text);
        JsonElement quiet = body.GetProperty("rows").EnumerateArray()
            .SelectMany(row => row.GetProperty("items").EnumerateArray())
            .First(item => item.GetProperty("tmdbId").GetInt32() == 804);
        Assert.Equal(JsonValueKind.Null, quiet.GetProperty("posterUrl").ValueKind);
        Assert.Equal("A quiet story.", quiet.GetProperty("overview").GetString());
        Assert.All(
            body.GetProperty("rows").EnumerateArray().SelectMany(row => row.GetProperty("items").EnumerateArray()),
            item => Assert.False(item.GetProperty("needsParent").GetBoolean()));
        Assert.DoesNotContain("The Matrix", text);
        Assert.DoesNotContain("Night Terror", text);
        Assert.DoesNotContain("Unrated Short", text);
        Assert.DoesNotContain("Secret plot", text);
        Assert.DoesNotContain("Grown Up Show", text);
        Assert.DoesNotContain("Unreachable Rating", text);
        Assert.DoesNotContain("image.tmdb.org", text);
        Assert.DoesNotContain("matrix.jpg", text);
        Assert.DoesNotContain("\"needsParent\":true", text);

        List<FakeTmdbServer.RecordedRequest> movies = [.. factory.Tmdb.Requests.Where(request => request.Path == "/3/discover/movie")];
        List<FakeTmdbServer.RecordedRequest> shows = [.. factory.Tmdb.Requests.Where(request => request.Path == "/3/discover/tv")];
        Assert.Equal(4, movies.Count);
        Assert.Equal(4, shows.Count);
        Assert.Equal(KidHome.MaxDiscoverRequests, movies.Count + shows.Count);
        FakeTmdbServer.RecordedRequest popular = Assert.Single(movies, request => !request.Query.ContainsKey("with_genres"));
        Assert.Equal("popularity.desc", popular.Query["sort_by"]);
        Assert.Equal("US", popular.Query["certification_country"]);
        Assert.Equal("G", popular.Query["certification.lte"]);
        Assert.Equal("1", popular.Query["page"]);
        Assert.Equal("false", popular.Query["include_adult"]);
        Assert.Equal("en-US", popular.Query["language"]);
        Assert.Equal(["10751", "16", "35"], movies.Where(request => request.Query.ContainsKey("with_genres")).Select(request => request.Query["with_genres"]).Order(StringComparer.Ordinal));
        Assert.All(movies, request => Assert.Equal("G", request.Query["certification.lte"]));
        Assert.All(shows, request =>
        {
            Assert.False(request.Query.ContainsKey("certification.lte"));
            Assert.False(request.Query.ContainsKey("certification_country"));
            Assert.Equal("false", request.Query["include_adult"]);
            Assert.Equal("popularity.desc", request.Query["sort_by"]);
        });
        Assert.Equal(1, factory.Tmdb.Count("/3/movie/800/release_dates"));
        Assert.Equal(1, factory.Tmdb.Count("/3/movie/806/release_dates"));

        int discovers = movies.Count + shows.Count;
        Assert.Equal(HttpStatusCode.OK, (await kid.GetAsync(HomePath)).StatusCode);
        Assert.Equal(discovers, factory.Tmdb.Count("/3/discover/movie") + factory.Tmdb.Count("/3/discover/tv"));
        Assert.Equal(1, factory.Tmdb.Count("/3/movie/800/release_dates"));
    }

    [Fact]
    public async Task A_keyword_block_and_a_failed_lookup_remove_the_title()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        factory.Tmdb.SetCatalog("/3/discover/movie", """
            {"page":1,"results":[
              {"id":800,"title":"Tiny Tale","poster_path":"/tiny.jpg","release_date":"2019-05-01","genre_ids":[16],"original_language":"en"},
              {"id":805,"title":"Keyword Monster","overview":"Has a monster.","poster_path":"/monster.jpg","release_date":"2020-02-02","genre_ids":[35],"original_language":"en"}
            ],"total_pages":1,"total_results":2}
            """);
        factory.Tmdb.SetCatalog("/3/discover/tv", """{"page":1,"results":[],"total_pages":1,"total_results":0}""");
        SetMovie(factory, 805, "G");
        factory.Tmdb.SetBody("/3/movie/800/keywords", """{"id":800,"keywords":[]}""");
        factory.Tmdb.SetBody("/3/movie/805/keywords", """{"id":805,"keywords":[{"id":99,"name":"monster"}]}""");
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 6, tvAgeLimit: 7, blockedKeywordIds: [99]);

        string text = await (await kid.GetAsync(HomePath)).Content.ReadAsStringAsync();

        Assert.Contains("Tiny Tale", text);
        Assert.DoesNotContain("Keyword Monster", text);
        Assert.DoesNotContain("Has a monster.", text);
        Assert.Equal(1, factory.Tmdb.Count("/3/movie/805/keywords"));
    }

    [Fact]
    public async Task A_language_allow_list_removes_other_languages()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        factory.Tmdb.SetCatalog("/3/discover/movie", """
            {"page":1,"results":[
              {"id":800,"title":"Tiny Tale","poster_path":"/tiny.jpg","release_date":"2019-05-01","genre_ids":[16],"original_language":"en"},
              {"id":804,"title":"Quiet Japanese","release_date":"2018-04-01","genre_ids":[16],"original_language":"ja"}
            ],"total_pages":1,"total_results":2}
            """);
        factory.Tmdb.SetCatalog("/3/discover/tv", """{"page":1,"results":[],"total_pages":1,"total_results":0}""");
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 6, allowedOriginalLanguages: ["en"]);

        string text = await (await kid.GetAsync(HomePath)).Content.ReadAsStringAsync();

        Assert.Contains("Tiny Tale", text);
        Assert.DoesNotContain("Quiet Japanese", text);
        Assert.DoesNotContain(factory.Tmdb.Requests, request => request.Path.EndsWith("/keywords", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discover_calls_stop_at_the_cap_and_later_rows_are_left_out()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(
            movieAgeLimit: 6,
            tvAgeLimit: 7,
            homeRows: ["genre:16", "genre:35", "genre:10751", "genre:12", "genre:18", "genre:14"]);

        JsonElement body = await (await kid.GetAsync(HomePath)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(["genre:16", "genre:35", "genre:10751", "genre:12"], RowIds(body));
        Assert.Equal(KidHome.MaxDiscoverRequests, factory.Tmdb.Count("/3/discover/movie") + factory.Tmdb.Count("/3/discover/tv"));
    }

    [Fact]
    public async Task Parents_choose_which_rows_appear_and_their_order()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 6, tvAgeLimit: 7, homeRows: ["genre:35", "popular", "library"]);

        JsonElement body = await (await kid.GetAsync(HomePath)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(["genre:35", "popular"], RowIds(body));
        Assert.Equal(35, body.GetProperty("rows")[0].GetProperty("genreId").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("rows")[1].GetProperty("genreId").ValueKind);
        Assert.DoesNotContain(factory.Tmdb.Requests, request => request.Query.TryGetValue("with_genres", out string? genres) && genres == "16");
        Assert.Contains(factory.Tmdb.Requests, request => request.Query.TryGetValue("with_genres", out string? genres) && genres == "35");
    }

    [Fact]
    public async Task An_empty_row_list_asks_tmdb_for_nothing()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(homeRows: []);

        JsonElement body = await (await kid.GetAsync(HomePath)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(RowIds(body));
        Assert.Equal(0, factory.Tmdb.Count("/3/discover/movie") + factory.Tmdb.Count("/3/discover/tv"));
    }

    [Fact]
    public async Task A_blocked_genre_is_not_fetched_as_its_own_row()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 6, tvAgeLimit: 7, blockedGenreIds: [16]);

        JsonElement body = await (await kid.GetAsync(HomePath)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.DoesNotContain("genre:16", RowIds(body));
        Assert.DoesNotContain(factory.Tmdb.Requests, request => request.Query.TryGetValue("with_genres", out string? genres) && genres == "16");
        Assert.DoesNotContain("Tiny Tale", await (await kid.GetAsync(HomePath)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Movie_certification_filter_follows_the_age_limit()
    {
        using KidRequestApiFactory open = PolicyFactory();
        (HttpClient anyone, _) = await open.SignInKidAsync(homeRows: ["popular"]);
        HttpResponseMessage openResponse = await anyone.GetAsync(HomePath);
        string openText = await openResponse.Content.ReadAsStringAsync();

        Assert.Contains("The Matrix", openText);
        Assert.All(
            open.Tmdb.Requests.Where(request => request.Path == "/3/discover/movie"),
            request => Assert.False(request.Query.ContainsKey("certification.lte")));

        using KidRequestApiFactory limited = PolicyFactory();
        (HttpClient kid, _) = await limited.SignInKidAsync(movieAgeLimit: 10, tvAgeLimit: 10, homeRows: ["popular"]);
        string limitedText = await (await kid.GetAsync(HomePath)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("The Matrix", limitedText);
        Assert.Contains("Tiny Tale", limitedText);
        Assert.All(
            limited.Tmdb.Requests.Where(request => request.Path == "/3/discover/movie"),
            request => Assert.Equal("PG", request.Query["certification.lte"]));
    }

    [Fact]
    public async Task New_in_our_library_uses_recent_titles_and_stops_after_eight_details()
    {
        FakeLibraryIndex index = new()
        {
            Recent = [.. Enumerable.Range(900, 10).Select(id => new LibraryCatalogEntry(MediaKind.Movie, id))],
        };
        index.Add(MediaKind.Movie, 900, new MediaItem("1", "plex://movie/900", "Shelf 900", 2024, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = PolicyFactory(index);
        for (int id = 900; id <= 907; id++)
        {
            string title = id == 901 ? "Night Library" : $"Shelf {id}";
            string rating = id == 901 ? "R" : "G";
            factory.Tmdb.SetBody(
                $"/3/movie/{id}",
                $$"""{"id":{{id}},"title":"{{title}}","overview":"On the shelf.","poster_path":"/shelf.jpg","release_date":"2024-01-01","genres":[{"id":35,"name":"Comedy"}],"original_language":"en","vote_average":7}""");
            SetMovie(factory, id, rating);
        }

        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 6, homeRows: ["library"]);
        HttpResponseMessage response = await kid.GetAsync(HomePath);
        string text = await response.Content.ReadAsStringAsync();
        JsonElement body = JsonDocument.Parse(text).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["library"], RowIds(body));
        Assert.Contains("Shelf 900", text);
        Assert.Contains("/api/v1/kid/images/tmdb/w342/shelf.jpg", text);
        Assert.DoesNotContain("Night Library", text);
        Assert.DoesNotContain("Plex 908", text);
        Assert.DoesNotContain("Plex 909", text);
        JsonElement shelf = body.GetProperty("rows")[0].GetProperty("items").EnumerateArray().First(item => item.GetProperty("tmdbId").GetInt32() == 900);
        Assert.Equal("inLibrary", shelf.GetProperty("availability").GetString());
        Assert.Equal(0, factory.Tmdb.Count("/3/discover/movie") + factory.Tmdb.Count("/3/discover/tv"));
        for (int id = 900; id <= 907; id++)
        {
            Assert.Equal(1, factory.Tmdb.Count($"/3/movie/{id}"));
        }

        Assert.Equal(0, factory.Tmdb.Count("/3/movie/908"));
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/909"));
    }

    [Fact]
    public async Task A_library_row_is_left_empty_when_the_index_cannot_answer()
    {
        using KidRequestApiFactory factory = PolicyFactory(new FakeLibraryIndex { Ready = false });
        (HttpClient kid, _) = await factory.SignInKidAsync(homeRows: ["library"]);

        HttpResponseMessage response = await kid.GetAsync(HomePath);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(RowIds(body));
        Assert.Equal(0, factory.Tmdb.Requests.Count(request => request.Path.StartsWith("/3/", StringComparison.Ordinal)));
        Assert.Equal(0, factory.LibraryScans);
    }

    [Fact]
    public async Task One_failed_discover_source_still_returns_the_sources_that_worked()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        factory.Tmdb.Enqueue("/3/discover/movie", 500);
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 6, tvAgeLimit: 7, homeRows: ["popular"]);

        HttpResponseMessage response = await kid.GetAsync(HomePath);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Bluey", text);
        Assert.DoesNotContain("TMDB answered", text);
    }

    [Fact]
    public async Task Home_is_unavailable_when_every_discover_call_fails_or_the_key_is_missing()
    {
        using KidRequestApiFactory failed = PolicyFactory();
        (HttpClient kid, _) = await failed.SignInKidAsync(movieAgeLimit: 6, tvAgeLimit: 7, homeRows: ["popular"]);
        failed.Tmdb.Enqueue("/3/discover/movie", 500);
        failed.Tmdb.Enqueue("/3/discover/tv", 500);

        HttpResponseMessage down = await kid.GetAsync(HomePath);
        string downText = await down.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
        Assert.Contains("Home isn't working right now.", downText);
        Assert.DoesNotContain("Tiny Tale", downText);
        Assert.DoesNotContain("TMDB answered", downText);

        using KidRequestApiFactory unconfigured = PolicyFactory();
        (HttpClient other, _) = await unconfigured.SignInKidAsync(homeRows: ["popular"]);
        ((FixedKeyProvider)unconfigured.Services.GetRequiredService<ITmdbApiKeyProvider>()).Key = null;

        HttpResponseMessage missing = await other.GetAsync(HomePath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        Assert.Contains("Home isn't working right now.", await missing.Content.ReadAsStringAsync());
        Assert.Equal(0, unconfigured.Tmdb.Count("/3/discover/movie"));
    }

    [Fact]
    public async Task Only_a_signed_in_kid_can_load_home()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        HttpClient anonymous = factory.CreateClient();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        (HttpClient kid, int profileId) = await factory.SignInKidAsync(movieAgeLimit: 6, tvAgeLimit: 7);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(HomePath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await parent.GetAsync(HomePath)).StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        KidProfile profile = await db.KidProfiles.SingleAsync(row => row.Id == profileId);
        profile.ArchivedAt = factory.Clock.GetUtcNow();
        await db.SaveChangesAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await kid.GetAsync(HomePath)).StatusCode);
        Assert.Equal(0, factory.Tmdb.Count("/3/discover/movie") + factory.Tmdb.Count("/3/discover/tv"));
    }

    [Fact]
    public async Task All_certification_lookups_failing_answers_ok_with_no_rows()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetCatalog("/3/discover/movie", PolicyMovies);
        factory.Tmdb.SetCatalog("/3/discover/tv", """{"page":1,"results":[{"id":810,"name":"Grown Up Show","first_air_date":"2019-01-01","genre_ids":[18],"original_language":"en"}],"total_pages":1,"total_results":1}""");
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 6, tvAgeLimit: 7, homeRows: ["popular"]);

        HttpResponseMessage response = await kid.GetAsync(HomePath);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(RowIds(JsonDocument.Parse(text).RootElement));
        Assert.DoesNotContain("Tiny Tale", text);
        Assert.DoesNotContain("Bluey", text);
    }

    [Fact]
    public async Task A_library_detail_that_fails_drops_only_that_title()
    {
        FakeLibraryIndex index = new()
        {
            Recent = [.. Enumerable.Range(900, 3).Select(id => new LibraryCatalogEntry(MediaKind.Movie, id))],
        };
        using KidRequestApiFactory factory = PolicyFactory(index);
        for (int id = 900; id <= 902; id++)
        {
            factory.Tmdb.SetBody(
                $"/3/movie/{id}",
                $$"""{"id":{{id}},"title":"Shelf {{id}}","overview":"On the shelf.","release_date":"2024-01-01","genres":[],"original_language":"en","vote_average":7}""");
            SetMovie(factory, id, "G");
        }

        factory.Tmdb.Enqueue("/3/movie/901", 500);
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 6, homeRows: ["library"]);

        HttpResponseMessage response = await kid.GetAsync(HomePath);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Shelf 900", text);
        Assert.DoesNotContain("Shelf 901", text);
        Assert.Contains("Shelf 902", text);
    }

    [Fact]
    public async Task A_library_detail_that_times_out_drops_only_that_title()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        (_, int profileId) = await factory.SignInKidAsync(homeRows: ["library"]);
        using IServiceScope scope = factory.Services.CreateScope();
        HomeClient tmdb = new() { TimeoutDetailId = 901 };
        FakeLibraryIndex index = new() { Recent = [.. Enumerable.Range(900, 3).Select(id => new LibraryCatalogEntry(MediaKind.Movie, id))] };
        KidHome home = NewHome(scope, tmdb, new ManualTimeProvider(), index);

        HomeLoad load = await home.LoadAsync(profileId);

        Assert.Equal(HomeLoadStatus.Ready, load.Status);
        KidHomeRow row = Assert.Single(load.Response!.Rows);
        Assert.Equal([900, 902], row.Items.Select(item => item.TmdbId).Order());
    }

    [Fact]
    public async Task Rows_that_outlive_the_budget_are_left_out_and_never_shown_unfiltered()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        (_, int profileId) = await factory.SignInKidAsync(movieAgeLimit: 6, homeRows: ["popular", "genre:35"]);
        using IServiceScope scope = factory.Services.CreateScope();
        HomeClient tmdb = new() { HangGenre = "35" };
        ManualTimeProvider time = new();
        KidHome home = NewHome(scope, tmdb, time);

        Task<HomeLoad> pending = home.LoadAsync(profileId);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));
        while (tmdb.HangingCalls < 1)
        {
            await Task.Delay(10, guard.Token);
        }

        Assert.False(pending.IsCompleted);
        time.Advance(KidContentPipeline.LookupBudget);
        HomeLoad load = await pending.WaitAsync(guard.Token);

        Assert.Equal(HomeLoadStatus.Ready, load.Status);
        KidHomeRow row = Assert.Single(load.Response!.Rows);
        Assert.Equal("popular", row.Id);
        Assert.DoesNotContain(load.Response.Rows.SelectMany(item => item.Items), item => item.Title == "Half Done");
        Assert.Equal(0, tmdb.HangingCalls);
    }

    [Fact]
    public async Task When_every_discover_call_outlives_the_budget_home_answers_with_no_rows()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        (_, int profileId) = await factory.SignInKidAsync(homeRows: ["popular", "genre:35"]);
        using IServiceScope scope = factory.Services.CreateScope();
        HomeClient tmdb = new() { HangGenre = "*" };
        ManualTimeProvider time = new();
        KidHome home = NewHome(scope, tmdb, time);

        Task<HomeLoad> pending = home.LoadAsync(profileId);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));
        while (tmdb.HangingCalls < 4)
        {
            await Task.Delay(10, guard.Token);
        }

        time.Advance(KidContentPipeline.LookupBudget);
        HomeLoad load = await pending.WaitAsync(guard.Token);

        Assert.Equal(HomeLoadStatus.Ready, load.Status);
        Assert.Empty(load.Response!.Rows);
    }

    [Fact]
    public async Task Home_waits_for_cancelled_calls_to_finish_before_it_answers()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        (_, int profileId) = await factory.SignInKidAsync(homeRows: ["popular", "genre:35"]);
        using IServiceScope scope = factory.Services.CreateScope();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HomeClient tmdb = new() { HangGenre = "*", CancelGate = gate };
        ManualTimeProvider time = new();
        KidHome home = NewHome(scope, tmdb, time);

        Task<HomeLoad> pending = home.LoadAsync(profileId);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));
        while (tmdb.HangingCalls < 4)
        {
            await Task.Delay(10, guard.Token);
        }

        time.Advance(KidContentPipeline.LookupBudget);
        while (tmdb.CancelledCalls < 4)
        {
            await Task.Delay(10, guard.Token);
        }

        await Task.Delay(100, guard.Token);
        Assert.False(pending.IsCompleted);

        gate.SetResult();
        HomeLoad load = await pending.WaitAsync(guard.Token);

        Assert.Equal(HomeLoadStatus.Ready, load.Status);
        Assert.Equal(0, tmdb.HangingCalls);
    }

    [Fact]
    public async Task A_call_that_fails_after_the_budget_is_observed()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        (_, int profileId) = await factory.SignInKidAsync(homeRows: ["popular", "genre:35"]);
        List<Exception> unobserved = [];
        void Record(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            lock (unobserved)
            {
                unobserved.AddRange(args.Exception.Flatten().InnerExceptions);
            }

            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Record;
        try
        {
            HomeLoad load = await LoadWithLateFailureAsync(factory, profileId);
            Assert.Equal(HomeLoadStatus.Ready, load.Status);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Record;
        }

        lock (unobserved)
        {
            Assert.DoesNotContain(unobserved, failure => failure is TmdbNotConfiguredException);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<HomeLoad> LoadWithLateFailureAsync(KidRequestApiFactory factory, int profileId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HomeClient tmdb = new() { HangGenre = "*", CancelGate = gate, LateNotConfigured = true };
        ManualTimeProvider time = new();
        KidHome home = NewHome(scope, tmdb, time);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        Task<HomeLoad> pending = home.LoadAsync(profileId);
        while (tmdb.HangingCalls < 4)
        {
            await Task.Delay(10, guard.Token);
        }

        time.Advance(KidContentPipeline.LookupBudget);
        while (tmdb.CancelledCalls < 4)
        {
            await Task.Delay(10, guard.Token);
        }

        gate.SetResult();
        return await pending.WaitAsync(guard.Token);
    }

    [Fact]
    public async Task A_genre_row_only_calls_discover_for_the_media_types_the_genre_has()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        (_, int profileId) = await factory.SignInKidAsync(homeRows: ["genre:10762", "genre:35", "genre:16", "genre:99"]);
        using IServiceScope scope = factory.Services.CreateScope();
        HomeClient tmdb = new()
        {
            Listed =
            [
                new TmdbListedGenre(10762, "Kids", false, true),
                new TmdbListedGenre(35, "Comedy", true, false),
                new TmdbListedGenre(16, "Animation", true, true),
            ],
        };
        KidHome home = NewHome(scope, tmdb, new ManualTimeProvider());

        await home.LoadAsync(profileId);

        TmdbMediaType[] TypesFor(string genre) =>
            [.. tmdb.Discovers.Where(call => call.Genres == genre).Select(call => call.Type).Order()];

        Assert.Equal([TmdbMediaType.Tv], TypesFor("10762"));
        Assert.Equal([TmdbMediaType.Movie], TypesFor("35"));
        Assert.Equal([TmdbMediaType.Movie, TmdbMediaType.Tv], TypesFor("16"));
        Assert.Equal([TmdbMediaType.Movie, TmdbMediaType.Tv], TypesFor("99"));
    }

    [Fact]
    public async Task Genre_rows_carry_the_tmdb_genre_name()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        factory.Tmdb.SetBody("/3/genre/movie/list", """{"genres":[{"id":35,"name":"Comedy"},{"id":16,"name":"Animation"}]}""");
        factory.Tmdb.SetBody("/3/genre/tv/list", """{"genres":[{"id":16,"name":"Animation"},{"id":10751,"name":"Family"}]}""");
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 6, tvAgeLimit: 7, homeRows: ["popular", "genre:10751"]);

        JsonElement body = await (await kid.GetAsync(HomePath)).Content.ReadFromJsonAsync<JsonElement>();

        JsonElement rows = body.GetProperty("rows");
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("genreName").ValueKind);
        Assert.Equal("Family", rows[1].GetProperty("genreName").GetString());
    }

    [Fact]
    public async Task Home_options_list_the_default_rows_and_genres_for_parents_only()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        factory.Tmdb.SetBody("/3/genre/movie/list", """{"genres":[{"id":35,"name":"Comedy"}]}""");
        factory.Tmdb.SetBody("/3/genre/tv/list", """{"genres":[{"id":37,"name":"Western"}]}""");
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        JsonElement options = await parent.GetFromJsonAsync<JsonElement>("/api/v1/kid-profiles/home-options");

        Assert.Equal(["popular", "library", "genre"], options.GetProperty("defaultRows").EnumerateArray().Select(row => row.GetString()));
        Assert.Equal(
            ["Comedy", "Western"],
            options.GetProperty("genres").EnumerateArray().Select(genre => genre.GetProperty("name").GetString()));
        Assert.Equal(
            [(true, false), (false, true)],
            options.GetProperty("genres").EnumerateArray().Select(genre => (genre.GetProperty("movie").GetBoolean(), genre.GetProperty("tv").GetBoolean())));
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync("/api/v1/kid-profiles/home-options")).StatusCode);

        using KidRequestApiFactory down = PolicyFactory();
        down.Tmdb.Enqueue("/3/genre/movie/list", 500);
        HttpClient other = await down.CreateAuthorizedClientAsync();
        JsonElement failed = await other.GetFromJsonAsync<JsonElement>("/api/v1/kid-profiles/home-options");
        Assert.Empty(failed.GetProperty("genres").EnumerateArray());
    }

    [Fact]
    public async Task A_kid_cannot_change_home_rows_and_a_parent_write_needs_the_antiforgery_token()
    {
        using KidRequestApiFactory factory = PolicyFactory();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        (HttpClient kid, int profileId) = await factory.SignInKidAsync(homeRows: ["popular"]);
        HttpClient noToken = factory.CreateClient();
        await noToken.PostAsJsonAsync("/api/v1/auth/login", new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword });
        object update = new { name = "Ada", avatar = "fox", homeRows = new[] { "library" } };
        string path = $"/api/v1/kid-profiles/{profileId}";

        HttpResponseMessage forbidden = await kid.PutAsJsonAsync(path, update);
        HttpResponseMessage rejected = await noToken.PutAsJsonAsync(path, update);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        JsonElement[] profiles = [.. (await parent.GetFromJsonAsync<JsonElement[]>("/api/v1/kid-profiles"))!];
        JsonElement stored = profiles.Single(profile => profile.GetProperty("id").GetInt32() == profileId);
        Assert.Equal(["popular"], stored.GetProperty("homeRows").EnumerateArray().Select(row => row.GetString()));
        Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync(path, update)).StatusCode);
    }

    private static KidHome NewHome(IServiceScope scope, ITmdbClient tmdb, TimeProvider time, FakeLibraryIndex? index = null) =>
        new(
            scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>(),
            tmdb,
            new KidContentPipeline(tmdb, [], time, NullLogger<KidContentPipeline>.Instance, scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>()),
            scope.ServiceProvider.GetRequiredService<DiscoveryLists>(),
            index is null ? [] : [index],
            time,
            NullLogger<KidHome>.Instance);

    private sealed class HomeClient : ITmdbClient
    {
        private int hanging;
        private int cancelled;

        public string? HangGenre { get; init; }

        public int TimeoutDetailId { get; init; }

        public IReadOnlyList<TmdbListedGenre> Listed { get; init; } = [new TmdbListedGenre(35, "Comedy", true, true)];

        public TaskCompletionSource? CancelGate { get; init; }

        public bool LateNotConfigured { get; init; }

        public ConcurrentQueue<(TmdbMediaType Type, string? Genres)> Discovers { get; } = [];

        public int CancelledCalls => Volatile.Read(ref this.cancelled);

        public int HangingCalls => Volatile.Read(ref this.hanging);

        public async Task<TmdbPage<TmdbSearchResult>> DiscoverAsync(TmdbMediaType type, TmdbDiscoverQuery query, string language = "en-US", CancellationToken ct = default)
        {
            bool movie = type == TmdbMediaType.Movie;
            this.Discovers.Enqueue((type, query.Genres));
            bool hang = this.HangGenre == "*" || (this.HangGenre is not null && query.Genres == this.HangGenre && !movie);
            if (hang)
            {
                Interlocked.Increment(ref this.hanging);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException) when (this.CancelGate is not null)
                {
                    Interlocked.Increment(ref this.cancelled);
                    await this.CancelGate.Task;
                    if (this.LateNotConfigured)
                    {
                        throw new TmdbNotConfiguredException();
                    }

                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref this.hanging);
                }
            }

            int id = query.Genres is null ? (movie ? 1 : 2) : (movie ? 3 : 4);
            string title = query.Genres is null ? "Safe" : "Half Done";
            return new TmdbPage<TmdbSearchResult>(
                1,
                [new TmdbSearchResult(id, null, movie ? title : null, movie ? null : title, null, null, null, null, null, 0)],
                1,
                1);
        }

        public Task<TmdbCertification?> GetCertificationAsync(TmdbMediaType type, int id, IReadOnlyList<string> regions, CancellationToken ct = default) =>
            Task.FromResult<TmdbCertification?>(new TmdbCertification("US", "G", []));

        public Task<IReadOnlyList<TmdbKeyword>> GetKeywordsAsync(TmdbMediaType type, int id, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<TmdbDetails> GetDetailsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default)
        {
            if (id == this.TimeoutDetailId)
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());
            }

            return Task.FromResult(new TmdbDetails(id, $"Shelf {id}", $"Shelf {id}", null, null, null, null, "2024-01-01", null, null, null, null, null, 0));
        }

        public Task<IReadOnlyList<TmdbListedGenre>> GetGenresAsync(string language = "en-US", CancellationToken ct = default) =>
            Task.FromResult(this.Listed);

        public Task<bool> TestApiKeyAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbPage<TmdbSearchResult>> SearchAsync(string query, TmdbMediaType? type = null, int page = 1, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbCredits> GetCreditsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbVideos> GetVideosAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbImages> GetImagesAsync(TmdbMediaType type, int id, string imageLanguages = "en,null", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbPage<TmdbSearchResult>> GetTrendingAsync(TmdbMediaType type, string window = "week", int page = 1, string language = "en-US", CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TmdbWatchProviders> GetWatchProvidersAsync(TmdbMediaType type, int id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int?> FindIdAsync(TmdbMediaType type, ExternalIdSource source, string externalId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
