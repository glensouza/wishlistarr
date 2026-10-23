using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Kids;
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
        factory.Tmdb.SetBody("/3/discover/movie", PolicyMovies);
        factory.Tmdb.SetBody("/3/discover/tv", PolicyShows);
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
        Assert.Equal(["16", "10751", "35"], movies.Where(request => request.Query.ContainsKey("with_genres")).Select(request => request.Query["with_genres"]));
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
        factory.Tmdb.SetBody("/3/discover/movie", """
            {"page":1,"results":[
              {"id":800,"title":"Tiny Tale","poster_path":"/tiny.jpg","release_date":"2019-05-01","genre_ids":[16],"original_language":"en"},
              {"id":805,"title":"Keyword Monster","overview":"Has a monster.","poster_path":"/monster.jpg","release_date":"2020-02-02","genre_ids":[35],"original_language":"en"}
            ],"total_pages":1,"total_results":2}
            """);
        factory.Tmdb.SetBody("/3/discover/tv", """{"page":1,"results":[],"total_pages":1,"total_results":0}""");
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
        factory.Tmdb.SetBody("/3/discover/movie", """
            {"page":1,"results":[
              {"id":800,"title":"Tiny Tale","poster_path":"/tiny.jpg","release_date":"2019-05-01","genre_ids":[16],"original_language":"en"},
              {"id":804,"title":"Quiet Japanese","release_date":"2018-04-01","genre_ids":[16],"original_language":"ja"}
            ],"total_pages":1,"total_results":2}
            """);
        factory.Tmdb.SetBody("/3/discover/tv", """{"page":1,"results":[],"total_pages":1,"total_results":0}""");
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
            Recent = [.. Enumerable.Range(900, 10).Select(id => new LibraryCatalogEntry(MediaKind.Movie, id, $"Plex {id}", 2024))],
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
}
