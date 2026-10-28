using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Requests;
using Wishlistarr.Tests.Tmdb;

namespace Wishlistarr.Tests.Search;

public sealed class KidTitleTests
{
    private const string MoviePath = "/api/v1/kid/titles/movie/800";
    private const string ShowPath = "/api/v1/kid/titles/show/82728";
    private const string TrailerSetting = "/api/v1/settings/kids.youtube_trailers";
    private const string OfficialTrailer = "M7lc1UVf-VE";

    private const string MovieDetails = """
        {"id":800,"title":"Tiny Tale","overview":"A gentle story about a fox.","poster_path":"/tiny.jpg","backdrop_path":"/tiny-bg.jpg","release_date":"2019-05-01","runtime":95,"genres":[{"id":16,"name":"Animation"}],"original_language":"en","adult":false,"vote_average":7}
        """;

    private const string Videos = """
        {"results":[
          {"id":"1","key":"vimeokey123","name":"Vimeo Trailer","site":"Vimeo","type":"Trailer","official":true},
          {"id":"2","key":"short","name":"Too short","site":"YouTube","type":"Trailer","official":true},
          {"id":"3","key":"has spaces!","name":"Spaces","site":"YouTube","type":"Trailer","official":true},
          {"id":"4","key":"abcdefghijk","name":"Unofficial","site":"YouTube","type":"Trailer","official":false},
          {"id":"5","key":"CCCCCCCCCCC","name":"A clip","site":"YouTube","type":"Clip","official":true},
          {"id":"6","key":"M7lc1UVf-VE","name":"Official trailer","site":"YouTube","type":"Trailer","official":true},
          {"id":"7","key":"DDDDDDDDDDD","name":"Later official","site":"YouTube","type":"Trailer","official":true}]}
        """;

    private const string Credits = """
        {"cast":[
          {"id":1,"name":"Ada Lovelace","character":"Captain","profile_path":"/ada.jpg"},
          {"id":2,"name":" ","character":"Nobody","profile_path":"/nobody.jpg"},
          {"id":3,"name":"Grace Hopper","character":" ","profile_path":"not a path"},
          {"id":4,"name":"Cast 04","character":"Four","profile_path":"/c04.jpg"},
          {"id":5,"name":"Cast 05","character":"Five","profile_path":"/c05.jpg"},
          {"id":6,"name":"Cast 06","character":"Six","profile_path":"/c06.jpg"},
          {"id":7,"name":"Cast 07","character":"Seven","profile_path":"/c07.jpg"},
          {"id":8,"name":"Cast 08","character":"Eight","profile_path":"/c08.jpg"},
          {"id":9,"name":"Cast 09","character":"Nine","profile_path":"/c09.jpg"},
          {"id":10,"name":"Cast 10","character":"Ten","profile_path":"/c10.jpg"},
          {"id":11,"name":"Cast 11","character":"Eleven","profile_path":"/c11.jpg"},
          {"id":12,"name":"Cast 12","character":"Twelve","profile_path":"/c12.jpg"},
          {"id":13,"name":"Cast 13","character":"Thirteen","profile_path":"/c13.jpg"},
          {"id":14,"name":"Thirteenth Cast","character":"Extra","profile_path":"/extra.jpg"}],
         "crew":[{"id":99,"name":"Director Name","job":"Director","department":"Directing"}]}
        """;

    private const string ShowDetails = """
        {"id":82728,"name":"Bluey","overview":"A puppy and her family.","poster_path":"/bluey.jpg","backdrop_path":"/bluey-bg.jpg","first_air_date":"2018-10-01","episode_run_time":[0,7],"genres":[{"id":16,"name":"Animation"}],"original_language":"en","vote_average":8,"seasons":[
          {"season_number":0,"name":"Specials","overview":"Short extras.","poster_path":"/specials.jpg","episode_count":2,"air_date":"2019-01-01"},
          {"season_number":2,"name":"Season 2","overview":"The second year.","poster_path":"/s2.jpg","episode_count":8,"air_date":"2020-03-17"},
          {"season_number":1,"name":" ","overview":"The first year.","poster_path":"/s1.jpg","episode_count":10,"air_date":"2018-10-01"},
          {"season_number":1,"name":"Duplicate","overview":"Ignore this duplicate.","poster_path":"/dup.jpg","episode_count":1,"air_date":"2018-10-01"},
          {"season_number":-1,"name":"Nope","overview":"Hidden season.","poster_path":"/nope.jpg","episode_count":1,"air_date":"2000-01-01"}]}
        """;

    private static KidRequestApiFactory CreateFactory(FakeLibraryIndex? index = null)
    {
        KidRequestApiFactory factory = new(index);
        factory.Tmdb.SetBody("/3/movie/800", MovieDetails);
        factory.Tmdb.SetBody("/3/movie/800/release_dates", Rating("PG"));
        factory.Tmdb.SetBody("/3/movie/800/credits", Credits);
        factory.Tmdb.SetBody("/3/movie/800/videos", Videos);
        factory.Tmdb.SetBody("/3/tv/82728", ShowDetails);
        factory.Tmdb.SetBody("/3/tv/82728/content_ratings", """{"results":[{"iso_3166_1":"US","rating":"TV-Y"}]}""");
        factory.Tmdb.SetBody("/3/tv/82728/credits", """{"cast":[{"id":1,"name":"Bluey Heeler","character":"Bluey","profile_path":"/bluey-face.jpg"}],"crew":[]}""");
        factory.Tmdb.SetBody("/3/tv/82728/videos", Videos);
        return factory;
    }

    [Fact]
    public async Task Fanart_key_is_secret_and_optional_artwork_is_proxied_for_allowed_titles()
    {
        using KidRequestApiFactory factory = CreateFactory();
        using FanartHandler handler = new();
        factory.FanartHandler = handler;
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        JsonElement before = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.Equal("/api/v1/kid/images/tmdb/w1280/tiny-bg.jpg", before.GetProperty("backdropUrl").GetString());
        Assert.Equal(0, handler.Calls);

        Assert.Equal(HttpStatusCode.NoContent, (await parent.PutAsJsonAsync("/api/v1/settings/fanart.api_key", new { value = "fixture-key", isSecret = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PutAsJsonAsync("/api/v1/settings/fanart.client_key", new { value = "fixture-personal", isSecret = false })).StatusCode);
        JsonElement settings = await (await parent.GetAsync("/api/v1/settings/")).Content.ReadFromJsonAsync<JsonElement>();
        JsonElement saved = settings.EnumerateArray().Single(row => row.GetProperty("key").GetString() == "fanart.api_key");
        Assert.True(saved.GetProperty("isSecret").GetBoolean());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("value").ValueKind);
        JsonElement personal = settings.EnumerateArray().Single(row => row.GetProperty("key").GetString() == "fanart.client_key");
        Assert.True(personal.GetProperty("isSecret").GetBoolean());
        Assert.Equal(JsonValueKind.Null, personal.GetProperty("value").ValueKind);
        JsonElement title = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.Contains(handler.Paths, path => path == "/v3/movies/800?api_key=fixture-key&client_key=fixture-personal");
        string background = title.GetProperty("backdropUrl").GetString()!;
        string logo = title.GetProperty("logoUrl").GetString()!;
        Assert.StartsWith("/api/v1/kid/images/fanart/full/", background);
        Assert.StartsWith("/api/v1/kid/images/fanart/bigpreview/", logo);
        Assert.Equal(HttpStatusCode.OK, (await kid.GetAsync(background)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await kid.GetAsync("/api/v1/kid/images/fanart/full/a")).StatusCode);
        factory.Tmdb.SetBody("/3/tv/82728/external_ids", """{"tvdb_id":353546}""");
        JsonElement show = await ReadAsync(await kid.GetAsync(ShowPath));
        Assert.StartsWith("/api/v1/kid/images/fanart/full/", show.GetProperty("backdropUrl").GetString());
        Assert.Contains(handler.Paths, path => path.StartsWith("/v3/tv/353546?", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PutAsJsonAsync("/api/v1/settings/fanart.api_key", new { value = "other", isSecret = false })).StatusCode);
        handler.Fail = true;
        JsonElement failed = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.Equal("/api/v1/kid/images/tmdb/w1280/tiny-bg.jpg", failed.GetProperty("backdropUrl").GetString());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("logoUrl").ValueKind);
        handler.Fail = false;
        await parent.PutAsJsonAsync("/api/v1/settings/fanart.api_key", new { value = "", isSecret = false });
        JsonElement personalOnly = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.StartsWith("/api/v1/kid/images/fanart/full/", personalOnly.GetProperty("backdropUrl").GetString());
        Assert.Contains(handler.Paths, path => path == "/v3/movies/800?client_key=fixture-personal");
        await parent.PutAsJsonAsync("/api/v1/settings/fanart.client_key", new { value = "", isSecret = false });
        JsonElement after = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.Equal("/api/v1/kid/images/tmdb/w1280/tiny-bg.jpg", after.GetProperty("backdropUrl").GetString());
    }

    [Fact]
    public async Task Blocked_titles_do_not_request_fanart()
    {
        using KidRequestApiFactory factory = CreateFactory();
        using FanartHandler handler = new();
        factory.FanartHandler = handler;
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 16, blockedGenreIds: [16]);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        await parent.PutAsJsonAsync("/api/v1/settings/fanart.api_key", new { value = "fixture-key", isSecret = false });
        Assert.Equal(HttpStatusCode.NotFound, (await kid.GetAsync(MoviePath)).StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class FanartHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<string> Paths { get; } = [];

        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            this.Calls++;
            this.Paths.Add(request.RequestUri!.PathAndQuery);
            if (this.Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (request.RequestUri!.Host == "assets.fanart.tv")
            {
                ByteArrayContent image = new([1, 2, 3]);
                image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = image });
            }

            string json = request.RequestUri!.AbsolutePath.StartsWith("/v3/tv/", StringComparison.Ordinal)
                ? """{"showbackground":[{"url":"https://assets.fanart.tv/fanart/tv/353546/showbackground/demo.jpg"}]}"""
                : """{"moviebackground":[{"url":"https://assets.fanart.tv/fanart/movies/800/moviebackground/demo.jpg"}],"hdmovielogo":[{"url":"https://assets.fanart.tv/fanart/movies/800/hdmovielogo/demo.png"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Trailer_source_order_selects_plex_then_youtube_and_respects_policy()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 800, new MediaItem("1", "plex://movie/a", "Tiny Tale", 2019, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = CreateFactory(index);
        FakeTrailers trailers = new();
        using FanartHandler fanart = new();
        factory.Trailers = trailers;
        factory.FanartHandler = fanart;
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        await parent.PutAsJsonAsync("/api/v1/settings/fanart.api_key", new { value = "fixture-key", isSecret = false });
        await SetTrailersAsync(factory, "true");
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PutAsJsonAsync("/api/v1/settings/kids.trailer_sources", new { value = "plex,youtube", isSecret = true })).StatusCode);
        JsonElement plex = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.Equal(MoviePath + "/trailer", plex.GetProperty("trailerUrl").GetString());
        Assert.Equal(JsonValueKind.Null, plex.GetProperty("trailerKey").ValueKind);
        int artworkCalls = fanart.Calls;
        int creditCalls = factory.Tmdb.Count("/3/movie/800/credits");
        HttpResponseMessage stream = await kid.GetAsync(MoviePath + "/trailer");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("video/mp4", stream.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await stream.Content.ReadAsByteArrayAsync());
        Assert.Equal(artworkCalls, fanart.Calls);
        Assert.Equal(creditCalls, factory.Tmdb.Count("/3/movie/800/credits"));

        await parent.PutAsJsonAsync("/api/v1/settings/kids.trailer_sources", new { value = "youtube,plex", isSecret = false });
        JsonElement youtube = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.Equal(OfficialTrailer, youtube.GetProperty("trailerKey").GetString());
        Assert.Equal(JsonValueKind.Null, youtube.GetProperty("trailerUrl").ValueKind);
        await parent.PutAsJsonAsync("/api/v1/settings/kids.trailer_sources", new { value = "plex,youtube", isSecret = false });

        trailers.Malformed = true;
        JsonElement malformed = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.Equal(OfficialTrailer, malformed.GetProperty("trailerKey").GetString());
        trailers.Malformed = false;
        trailers.Available = false;
        JsonElement fallback = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.Equal(OfficialTrailer, fallback.GetProperty("trailerKey").GetString());
        Assert.Equal(JsonValueKind.Null, fallback.GetProperty("trailerUrl").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await kid.GetAsync(MoviePath + "/trailer")).StatusCode);

        await parent.PutAsJsonAsync("/api/v1/settings/kids.trailer_sources", new { value = "none", isSecret = false });
        JsonElement none = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("trailerKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("trailerUrl").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PutAsJsonAsync("/api/v1/settings/kids.trailer_sources", new { value = "plex,plex", isSecret = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PutAsJsonAsync("/api/v1/settings/kids.trailer_sources", new { value = "plex", isSecret = false })).StatusCode);
    }

    [Fact]
    public async Task Blocked_titles_cannot_stream_a_plex_extra()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 800, new MediaItem("1", "plex://movie/a", "Tiny Tale", 2019, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = CreateFactory(index);
        FakeTrailers trailers = new();
        factory.Trailers = trailers;
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 16, blockedGenreIds: [16]);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        await parent.PutAsJsonAsync("/api/v1/settings/kids.trailer_sources", new { value = "plex", isSecret = false });
        Assert.Equal(HttpStatusCode.NotFound, (await kid.GetAsync(MoviePath + "/trailer")).StatusCode);
        Assert.Equal(0, trailers.Calls);
    }

    private sealed class FakeTrailers : IMediaServerTrailers
    {
        public bool Available { get; set; } = true;

        public bool Malformed { get; set; }

        public int Calls { get; private set; }

        public Task<bool> HasTrailerAsync(string itemId, CancellationToken ct = default)
        {
            this.Calls++;
            return this.Malformed ? Task.FromException<bool>(new JsonException()) : Task.FromResult(this.Available);
        }

        public Task<HttpResponseMessage?> OpenTrailerAsync(string itemId, string? range, CancellationToken ct = default) =>
            Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
    }

    private static string Rating(string certification) =>
        $$"""{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"{{certification}}","type":3}]}]}""";

    private static async Task SetTrailersAsync(KidRequestApiFactory factory, string value, HttpStatusCode expected = HttpStatusCode.NoContent)
    {
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpResponseMessage response = await parent.PutAsJsonAsync(TrailerSetting, new { value, isSecret = true });
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task An_allowed_movie_returns_artwork_synopsis_cast_rating_runtime_and_one_youtube_trailer()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        await SetTrailersAsync(factory, "TRUE");

        HttpResponseMessage response = await kid.GetAsync(MoviePath);
        string text = await response.Content.ReadAsStringAsync();
        JsonElement body = JsonDocument.Parse(text).RootElement;

        Assert.Equal("movie", body.GetProperty("kind").GetString());
        Assert.Equal(800, body.GetProperty("tmdbId").GetInt32());
        Assert.Equal("Tiny Tale", body.GetProperty("title").GetString());
        Assert.Equal("2019", body.GetProperty("year").GetString());
        Assert.Equal("A gentle story about a fox.", body.GetProperty("overview").GetString());
        Assert.Equal("/api/v1/kid/images/tmdb/w342/tiny.jpg", body.GetProperty("posterUrl").GetString());
        Assert.Equal("/api/v1/kid/images/tmdb/w1280/tiny-bg.jpg", body.GetProperty("backdropUrl").GetString());
        Assert.Equal(95, body.GetProperty("runtimeMinutes").GetInt32());
        Assert.Equal("PG", body.GetProperty("rating").GetString());
        Assert.False(body.GetProperty("needsParent").GetBoolean());
        Assert.Equal(OfficialTrailer, body.GetProperty("trailerKey").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("request").ValueKind);
        Assert.Empty(body.GetProperty("seasons").EnumerateArray());
        Assert.Equal(12, body.GetProperty("cast").GetArrayLength());
        Assert.Equal("Ada Lovelace", body.GetProperty("cast")[0].GetProperty("name").GetString());
        Assert.Equal("Captain", body.GetProperty("cast")[0].GetProperty("character").GetString());
        Assert.Equal("/api/v1/kid/images/tmdb/w185/ada.jpg", body.GetProperty("cast")[0].GetProperty("profileUrl").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cast")[1].GetProperty("character").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cast")[1].GetProperty("profileUrl").ValueKind);
        Assert.DoesNotContain("Thirteenth Cast", text);
        Assert.DoesNotContain("Director Name", text);
        Assert.DoesNotContain("vimeokey123", text);
        Assert.DoesNotContain("abcdefghijk", text);
        Assert.DoesNotContain("CCCCCCCCCCC", text);
        Assert.DoesNotContain("DDDDDDDDDDD", text);
        Assert.DoesNotContain("youtube.com", text);
        Assert.DoesNotContain("ytimg", text);
        Assert.DoesNotContain("image.tmdb.org", text);
        Assert.True(response.Headers.CacheControl is { NoStore: true, Private: true });
        Assert.Equal(1, factory.Tmdb.Count("/3/movie/800/videos"));
    }

    [Fact]
    public async Task Trailers_stay_out_until_a_parent_turns_them_on_and_only_true_or_false_is_stored()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);

        JsonElement off = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.Equal(JsonValueKind.Null, off.GetProperty("trailerKey").ValueKind);
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/800/videos"));

        await SetTrailersAsync(factory, "yes", HttpStatusCode.BadRequest);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        JsonElement settings = await parent.GetFromJsonAsync<JsonElement>("/api/v1/settings/");
        Assert.DoesNotContain(settings.EnumerateArray(), item => item.GetProperty("key").GetString() == "kids.youtube_trailers");

        await SetTrailersAsync(factory, "false");
        JsonElement stillOff = await ReadAsync(await kid.GetAsync(MoviePath));
        Assert.Equal(JsonValueKind.Null, stillOff.GetProperty("trailerKey").ValueKind);
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/800/videos"));

        await SetTrailersAsync(factory, "true");
        JsonElement stored = await parent.GetFromJsonAsync<JsonElement>("/api/v1/settings/");
        JsonElement setting = stored.EnumerateArray().Single(item => item.GetProperty("key").GetString() == "kids.youtube_trailers");
        Assert.Equal("true", setting.GetProperty("value").GetString());
        Assert.False(setting.GetProperty("isSecret").GetBoolean());
        Assert.Equal(OfficialTrailer, (await ReadAsync(await kid.GetAsync(MoviePath))).GetProperty("trailerKey").GetString());

        await SetTrailersAsync(factory, "false");
        string disabled = await (await kid.GetAsync(MoviePath)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(OfficialTrailer, disabled);
        Assert.DoesNotContain("Official trailer", disabled);
    }

    [Fact]
    public async Task A_blocked_title_is_not_found_and_the_body_does_not_say_why()
    {
        using KidRequestApiFactory factory = CreateFactory();
        factory.Tmdb.SetBody("/3/movie/801", """
            {"id":801,"title":"Night Terror","overview":"A frightening plot.","poster_path":"/terror.jpg","backdrop_path":"/terror-bg.jpg","release_date":"2021-10-31","runtime":110,"genres":[{"id":27,"name":"Horror"}],"original_language":"en","vote_average":6}
            """);
        factory.Tmdb.SetBody("/3/movie/801/release_dates", Rating("R"));
        factory.Tmdb.SetBody("/3/movie/801/videos", Videos);
        factory.Tmdb.SetBody("/3/movie/801/credits", Credits);
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        await SetTrailersAsync(factory, "true");

        HttpResponseMessage blocked = await kid.GetAsync("/api/v1/kid/titles/movie/801");
        HttpResponseMessage missing = await kid.GetAsync("/api/v1/kid/titles/movie/999001");
        string blockedText = await blocked.Content.ReadAsStringAsync();
        JsonElement blockedBody = JsonDocument.Parse(blockedText).RootElement;
        JsonElement missingBody = await missing.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(missingBody.GetProperty("title").GetString(), blockedBody.GetProperty("title").GetString());
        Assert.Equal(missingBody.GetProperty("status").GetInt32(), blockedBody.GetProperty("status").GetInt32());
        Assert.DoesNotContain("Night Terror", blockedText);
        Assert.DoesNotContain("frightening", blockedText);
        Assert.DoesNotContain("terror.jpg", blockedText);
        Assert.DoesNotContain(OfficialTrailer, blockedText);
        Assert.DoesNotContain("AboveRatingLimit", blockedText);
        Assert.DoesNotContain("Horror", blockedText);
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/801/videos"));
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/801/credits"));
        Assert.True(blocked.Headers.CacheControl is { NoStore: true, Private: true });
    }

    [Fact]
    public async Task Genre_and_language_blocks_hide_the_title_the_same_way_a_missing_one_does()
    {
        using KidRequestApiFactory genres = CreateFactory();
        (HttpClient genreKid, _) = await genres.SignInKidAsync("Genres", movieAgeLimit: 16, blockedGenreIds: [16]);
        using KidRequestApiFactory languages = CreateFactory();
        (HttpClient languageKid, _) = await languages.SignInKidAsync("Languages", movieAgeLimit: 16, allowedOriginalLanguages: ["ja"]);

        HttpResponseMessage genre = await genreKid.GetAsync(MoviePath);
        HttpResponseMessage language = await languageKid.GetAsync(MoviePath);

        Assert.Equal(HttpStatusCode.NotFound, genre.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, language.StatusCode);
        Assert.DoesNotContain("Tiny Tale", await genre.Content.ReadAsStringAsync());
        Assert.DoesNotContain("gentle story", await language.Content.ReadAsStringAsync());
        Assert.Equal(0, genres.Tmdb.Count("/3/movie/800/credits"));
        Assert.Equal(0, languages.Tmdb.Count("/3/movie/800/videos"));
    }

    [Fact]
    public async Task A_failed_rating_or_keyword_lookup_hides_the_title()
    {
        using KidRequestApiFactory ratings = CreateFactory();
        ratings.Tmdb.Enqueue("/3/movie/800/release_dates", 500);
        (HttpClient ratingKid, _) = await ratings.SignInKidAsync(movieAgeLimit: 10);
        using KidRequestApiFactory keywords = CreateFactory();
        keywords.Tmdb.Enqueue("/3/movie/800/keywords", 500);
        (HttpClient keywordKid, _) = await keywords.SignInKidAsync(movieAgeLimit: 16, blockedKeywordIds: [99]);

        HttpResponseMessage rating = await ratingKid.GetAsync(MoviePath);
        HttpResponseMessage keyword = await keywordKid.GetAsync(MoviePath);

        Assert.Equal(HttpStatusCode.NotFound, rating.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, keyword.StatusCode);
        Assert.DoesNotContain("Tiny Tale", await rating.Content.ReadAsStringAsync());
        Assert.DoesNotContain("gentle story", await keyword.Content.ReadAsStringAsync());
        Assert.Equal(0, ratings.Tmdb.Count("/3/movie/800/videos"));
        Assert.Equal(0, keywords.Tmdb.Count("/3/movie/800/credits"));
    }

    [Fact]
    public async Task A_title_that_needs_a_parent_is_masked_and_has_no_trailer()
    {
        using KidRequestApiFactory factory = CreateFactory();
        factory.Tmdb.SetBody("/3/movie/800/release_dates", """{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"","type":3}]}]}""");
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        await SetTrailersAsync(factory, "true");

        string text = await (await kid.GetAsync(MoviePath)).Content.ReadAsStringAsync();
        JsonElement body = JsonDocument.Parse(text).RootElement;

        Assert.True(body.GetProperty("needsParent").GetBoolean());
        Assert.Equal("Tiny Tale", body.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("overview").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("posterUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("backdropUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("trailerKey").ValueKind);
        Assert.Empty(body.GetProperty("cast").EnumerateArray());
        Assert.DoesNotContain("gentle story", text);
        Assert.DoesNotContain("tiny.jpg", text);
        Assert.DoesNotContain(OfficialTrailer, text);
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/800/videos"));
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/800/credits"));
    }

    [Fact]
    public async Task An_adult_title_is_not_shown()
    {
        using KidRequestApiFactory factory = CreateFactory();
        factory.Tmdb.SetBody("/3/movie/800", """
            {"id":800,"title":"Tiny Tale","overview":"Grown up plot.","adult":true,"release_date":"2019-05-01","runtime":95,"genres":[{"id":16,"name":"Animation"}],"original_language":"en","vote_average":7}
            """);
        (HttpClient kid, _) = await factory.SignInKidAsync();

        HttpResponseMessage response = await kid.GetAsync(MoviePath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Grown up plot", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/800/credits"));
    }

    [Fact]
    public async Task A_profile_without_keyword_blocks_does_not_ask_for_keywords()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);

        Assert.Equal(HttpStatusCode.OK, (await kid.GetAsync(MoviePath)).StatusCode);
        Assert.DoesNotContain(factory.Tmdb.Requests, request => request.Path.EndsWith("/keywords", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shows_list_seasons_in_order_and_use_the_episode_runtime()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(tvAgeLimit: 7);

        string text = await (await kid.GetAsync(ShowPath)).Content.ReadAsStringAsync();
        JsonElement body = JsonDocument.Parse(text).RootElement;
        JsonElement seasons = body.GetProperty("seasons");

        Assert.Equal("show", body.GetProperty("kind").GetString());
        Assert.Equal("Bluey", body.GetProperty("title").GetString());
        Assert.Equal(7, body.GetProperty("runtimeMinutes").GetInt32());
        Assert.Equal("TV-Y", body.GetProperty("rating").GetString());
        Assert.Equal([0, 1, 2], seasons.EnumerateArray().Select(item => item.GetProperty("number").GetInt32()));
        Assert.Equal("Specials", seasons[0].GetProperty("name").GetString());
        Assert.Equal("Short extras.", seasons[0].GetProperty("overview").GetString());
        Assert.Equal("/api/v1/kid/images/tmdb/w185/specials.jpg", seasons[0].GetProperty("posterUrl").GetString());
        Assert.Equal(2, seasons[0].GetProperty("episodeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, seasons[1].GetProperty("name").ValueKind);
        Assert.Equal("The first year.", seasons[1].GetProperty("overview").GetString());
        Assert.Equal(10, seasons[1].GetProperty("episodeCount").GetInt32());
        Assert.Equal("2018", seasons[1].GetProperty("year").GetString());
        Assert.Equal("Season 2", seasons[2].GetProperty("name").GetString());
        Assert.DoesNotContain("Duplicate", text);
        Assert.DoesNotContain("Hidden season", text);
        Assert.DoesNotContain("Nope", text);
    }

    [Fact]
    public async Task A_show_that_needs_a_parent_keeps_season_counts_and_hides_the_story()
    {
        using KidRequestApiFactory factory = CreateFactory();
        factory.Tmdb.SetBody("/3/tv/82728/content_ratings", """{"results":[]}""");
        (HttpClient kid, _) = await factory.SignInKidAsync(tvAgeLimit: 7);
        await SetTrailersAsync(factory, "true");

        string text = await (await kid.GetAsync(ShowPath)).Content.ReadAsStringAsync();
        JsonElement season = JsonDocument.Parse(text).RootElement.GetProperty("seasons")[1];

        Assert.Equal(1, season.GetProperty("number").GetInt32());
        Assert.Equal(10, season.GetProperty("episodeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, season.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, season.GetProperty("overview").ValueKind);
        Assert.Equal(JsonValueKind.Null, season.GetProperty("posterUrl").ValueKind);
        Assert.DoesNotContain("first year", text);
        Assert.DoesNotContain("puppy", text);
        Assert.DoesNotContain(OfficialTrailer, text);
    }

    [Fact]
    public async Task The_request_on_a_title_follows_the_open_request_and_ignores_a_denied_one()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, int profileId) = await factory.SignInKidAsync(movieAgeLimit: 10);

        Assert.Equal(JsonValueKind.Null, (await ReadAsync(await kid.GetAsync(MoviePath))).GetProperty("request").ValueKind);

        HttpResponseMessage created = await KidRequestApiFactory.RequestAsync(kid, "movie", 800);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonElement pending = (await ReadAsync(await kid.GetAsync(MoviePath))).GetProperty("request");
        Assert.Equal("pending", pending.GetProperty("state").GetString());
        Assert.Equal(1, pending.GetProperty("requestCount").GetInt32());

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            MediaRequest request = await db.MediaRequests.SingleAsync(row => row.KidProfileId == profileId);
            request.State = RequestState.Failed;
            await db.SaveChangesAsync();
        }

        Assert.Equal("failed", (await ReadAsync(await kid.GetAsync(MoviePath))).GetProperty("request").GetProperty("state").GetString());

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            MediaRequest request = await db.MediaRequests.SingleAsync(row => row.KidProfileId == profileId);
            request.State = RequestState.Labeled;
            await db.SaveChangesAsync();
        }

        Assert.Equal("labeled", (await ReadAsync(await kid.GetAsync(MoviePath))).GetProperty("request").GetProperty("state").GetString());

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            MediaRequest request = await db.MediaRequests.SingleAsync(row => row.KidProfileId == profileId);
            request.State = RequestState.Denied;
            await db.SaveChangesAsync();
        }

        Assert.Equal(JsonValueKind.Null, (await ReadAsync(await kid.GetAsync(MoviePath))).GetProperty("request").ValueKind);
    }

    [Fact]
    public async Task Availability_comes_from_the_library_index_and_never_asks_plex()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 800, new MediaItem("1", "plex://movie/a", "Tiny Tale", 2019, MediaKind.Movie, "1", []));
        using KidRequestApiFactory ready = CreateFactory(index);
        (HttpClient kid, _) = await ready.SignInKidAsync(movieAgeLimit: 10);
        using KidRequestApiFactory without = CreateFactory();
        (HttpClient other, _) = await without.SignInKidAsync(movieAgeLimit: 10);
        using KidRequestApiFactory notReady = CreateFactory(new FakeLibraryIndex { Ready = false });
        (HttpClient waiting, _) = await notReady.SignInKidAsync(movieAgeLimit: 10);

        Assert.Equal("inLibrary", (await ReadAsync(await kid.GetAsync(MoviePath))).GetProperty("availability").GetString());
        Assert.Equal("notInLibrary", (await ReadAsync(await kid.GetAsync(ShowPath))).GetProperty("availability").GetString());
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(await other.GetAsync(MoviePath))).GetProperty("availability").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(await waiting.GetAsync(MoviePath))).GetProperty("availability").ValueKind);
        Assert.Equal(0, ready.LibraryScans);
        Assert.Equal(0, without.LibraryScans);
        Assert.Equal(0, notReady.LibraryScans);
    }

    [Fact]
    public async Task Missing_or_disallowed_artwork_is_left_out_instead_of_a_remote_url()
    {
        using KidRequestApiFactory factory = CreateFactory();
        factory.Tmdb.SetBody("/3/movie/800", """
            {"id":800,"title":"Tiny Tale","overview":"A gentle story about a fox.","poster_path":"../secret.jpg","backdrop_path":"/notes.txt","release_date":"2019-05-01","runtime":95,"genres":[{"id":16,"name":"Animation"}],"original_language":"en","vote_average":7}
            """);
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);

        string text = await (await kid.GetAsync(MoviePath)).Content.ReadAsStringAsync();
        JsonElement body = JsonDocument.Parse(text).RootElement;

        Assert.Equal(JsonValueKind.Null, body.GetProperty("posterUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("backdropUrl").ValueKind);
        Assert.DoesNotContain("image.tmdb.org", text);
        Assert.DoesNotContain("secret.jpg", text);
        Assert.DoesNotContain("notes.txt", text);
    }

    [Fact]
    public async Task A_video_lookup_failure_omits_the_trailer_and_still_returns_the_title()
    {
        using KidRequestApiFactory factory = CreateFactory();
        factory.Tmdb.Enqueue("/3/movie/800/videos", 500);
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        await SetTrailersAsync(factory, "true");

        JsonElement body = await ReadAsync(await kid.GetAsync(MoviePath));

        Assert.Equal("Tiny Tale", body.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("trailerKey").ValueKind);
        Assert.Equal("Ada Lovelace", body.GetProperty("cast")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Only_a_signed_in_kid_can_open_a_title()
    {
        using KidRequestApiFactory factory = CreateFactory();
        HttpClient anonymous = factory.CreateClient();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(MoviePath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await parent.GetAsync(MoviePath)).StatusCode);
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/800"));
    }

    [Fact]
    public async Task Only_a_parent_can_change_the_trailer_setting()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        HttpClient anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PutAsJsonAsync(TrailerSetting, new { value = "true", isSecret = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(TrailerSetting, new { value = "true", isSecret = false })).StatusCode);

        HttpResponseMessage response = await kid.GetAsync(MoviePath);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(response)).GetProperty("trailerKey").ValueKind);
    }

    [Fact]
    public async Task A_bad_kind_does_not_call_tmdb()
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await kid.GetAsync("/api/v1/kid/titles/tv/800")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await kid.GetAsync("/api/v1/kid/titles/movie/0")).StatusCode);
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/800"));
        Assert.Equal(0, factory.Tmdb.Count("/3/tv/800"));
    }

    [Fact]
    public async Task Tmdb_failures_are_kid_safe()
    {
        using KidRequestApiFactory down = CreateFactory();
        down.Tmdb.Enqueue("/3/movie/800", 500);
        (HttpClient kid, _) = await down.SignInKidAsync();
        using KidRequestApiFactory unconfigured = CreateFactory();
        (HttpClient other, _) = await unconfigured.SignInKidAsync();
        ((FixedKeyProvider)unconfigured.Services.GetRequiredService<ITmdbApiKeyProvider>()).Key = null;

        HttpResponseMessage failed = await kid.GetAsync(MoviePath);
        HttpResponseMessage missingKey = await other.GetAsync(MoviePath);

        Assert.Equal(HttpStatusCode.NotFound, failed.StatusCode);
        Assert.Contains("We couldn't find that one.", await failed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missingKey.StatusCode);
        Assert.DoesNotContain("api_key", await missingKey.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("w185")]
    [InlineData("w342")]
    [InlineData("w1280")]
    public async Task Kid_artwork_sizes_are_proxied(string size)
    {
        using KidRequestApiFactory factory = CreateFactory();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        HttpResponseMessage image = await kid.GetAsync($"/api/v1/kid/images/tmdb/{size}/tiny.jpg");

        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(FakeTmdbServer.ImageBytes, await image.Content.ReadAsByteArrayAsync());
    }
}
