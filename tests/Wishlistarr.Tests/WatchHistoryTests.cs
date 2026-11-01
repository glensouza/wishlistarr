using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tautulli;
using Wishlistarr.Tests.Plex;
using Wishlistarr.Tests.Requests;

namespace Wishlistarr.Tests;

public sealed class WatchHistoryTests
{
    [Fact]
    public async Task History_is_profile_specific_and_recommendations_use_only_finished_titles()
    {
        using WatchFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int profile) = await factory.SignInKidAsync(homeRows: ["popular"]);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        JsonElement unknown = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{profile}/watch-history");
        Assert.False(unknown.GetProperty("available").GetBoolean());
        Assert.Equal(0, factory.Handler.Requests);
        await factory.SeedAsync(profile, "42");
        factory.Tmdb.SetBody("/3/movie/700/recommendations", """{"page":1,"results":[{"id":701,"title":"A gentle adventure","genre_ids":[16],"original_language":"en"}],"total_pages":1,"total_results":1}""");
        JsonElement history = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{profile}/watch-history");
        Assert.True(history.GetProperty("available").GetBoolean());
        JsonElement[] titles = [.. history.GetProperty("titles").EnumerateArray()];
        Assert.Equal(2, titles.Length);
        Assert.True(titles.Single(t => t.GetProperty("tmdbId").GetInt32() == 700).GetProperty("finished").GetBoolean());
        Assert.False(titles.Single(t => t.GetProperty("tmdbId").GetInt32() == 702).GetProperty("finished").GetBoolean());
        Assert.Equal(35, titles.Single(t => t.GetProperty("tmdbId").GetInt32() == 702).GetProperty("percentComplete").GetInt32());
        HttpResponseMessage response = await kid.GetAsync("/api/v1/kid/home");
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("A gentle adventure", body);
        Assert.Contains("\"id\":\"watched\"", body);
        Assert.Contains(factory.Tmdb.Requests, request => request.Path == "/3/movie/700/recommendations");
        Assert.DoesNotContain(factory.Tmdb.Requests, request => request.Path == "/3/movie/702/recommendations");
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync($"/api/v1/kid-profiles/{profile}/watch-history")).StatusCode);
    }

    [Fact]
    public async Task Mapped_profiles_get_their_own_history_and_recommendation_seed()
    {
        using WatchFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient firstKid, int first) = await factory.SignInKidAsync(name: "Profile One", homeRows: ["popular"]);
        (HttpClient secondKid, int second) = await factory.SignInKidAsync(name: "Profile Two", homeRows: ["popular"]);
        await factory.SeedAsync(first, "42");
        await factory.SeedAsync(second, "43", seedIndex: false);
        factory.Tmdb.SetBody("/3/movie/700/recommendations", """{"page":1,"results":[{"id":701,"title":"First recommendation","genre_ids":[16],"original_language":"en"}],"total_pages":1,"total_results":1}""");
        factory.Tmdb.SetBody("/3/movie/702/recommendations", """{"page":1,"results":[{"id":703,"title":"Second recommendation","genre_ids":[16],"original_language":"en"}],"total_pages":1,"total_results":1}""");
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        JsonElement firstHistory = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{first}/watch-history");
        JsonElement secondHistory = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{second}/watch-history");
        Assert.True(firstHistory.GetProperty("titles").EnumerateArray().Single(t => t.GetProperty("tmdbId").GetInt32() == 700).GetProperty("finished").GetBoolean());
        Assert.Equal(702, Assert.Single(secondHistory.GetProperty("titles").EnumerateArray()).GetProperty("tmdbId").GetInt32());
        string firstHome = await (await firstKid.GetAsync("/api/v1/kid/home")).Content.ReadAsStringAsync();
        string secondHome = await (await secondKid.GetAsync("/api/v1/kid/home")).Content.ReadAsStringAsync();
        Assert.Contains("First recommendation", firstHome);
        Assert.DoesNotContain("Second recommendation", firstHome);
        Assert.Contains("Second recommendation", secondHome);
        Assert.DoesNotContain("First recommendation", secondHome);
    }

    [Fact]
    public async Task Later_page_keeps_a_finished_play_when_the_latest_play_is_partial()
    {
        using WatchFactory factory = new();
        await factory.LinkPlexAsync();
        (_, int profile) = await factory.SignInKidAsync();
        await factory.SeedAsync(profile, "42");
        factory.Handler.Paginated = true;
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        JsonElement history = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{profile}/watch-history");
        JsonElement title = Assert.Single(history.GetProperty("titles").EnumerateArray());
        Assert.Equal(700, title.GetProperty("tmdbId").GetInt32());
        Assert.True(title.GetProperty("finished").GetBoolean());
        Assert.Equal(35, title.GetProperty("percentComplete").GetInt32());
        Assert.Equal(299, title.GetProperty("lastPlayed").GetInt64());
        Assert.True(factory.Handler.SecondPageRequested);
    }

    [Fact]
    public async Task Recommendations_are_filtered_by_the_profiles_content_policy()
    {
        using WatchFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int profile) = await factory.SignInKidAsync(movieAgeLimit: 6, homeRows: ["popular"]);
        await factory.SeedAsync(profile, "42");
        factory.Tmdb.SetBody("/3/movie/700/recommendations", """{"page":1,"results":[{"id":800,"title":"Gentle pick","genre_ids":[16],"original_language":"en"},{"id":801,"title":"Restricted pick","genre_ids":[27],"original_language":"en"}],"total_pages":1,"total_results":2}""");
        factory.Tmdb.SetBody("/3/movie/800", """{"id":800,"title":"Gentle pick","genres":[{"id":16,"name":"Animation"}],"original_language":"en"}""");
        factory.Tmdb.SetBody("/3/movie/801", """{"id":801,"title":"Restricted pick","genres":[{"id":27,"name":"Horror"}],"original_language":"en"}""");
        factory.Tmdb.SetBody("/3/movie/800/release_dates", """{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"G","type":3}]}]}""");
        factory.Tmdb.SetBody("/3/movie/801/release_dates", """{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"R","type":3}]}]}""");
        HttpResponseMessage response = await kid.GetAsync("/api/v1/kid/home");
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Gentle pick", body);
        Assert.DoesNotContain("Restricted pick", body);
        Assert.Contains(factory.Tmdb.Requests, request => request.Path == "/3/movie/801/release_dates");
    }

    [Fact]
    public async Task Partial_plays_do_not_seed_recommendations_and_failures_leave_home_working()
    {
        using WatchFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int profile) = await factory.SignInKidAsync(homeRows: ["popular"]);
        await factory.SeedAsync(profile, "42");
        factory.Handler.PartialOnly = true;
        string body = await (await kid.GetAsync("/api/v1/kid/home")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"id\":\"watched\"", body);
        Assert.DoesNotContain(factory.Tmdb.Requests, request => request.Path.EndsWith("/recommendations", StringComparison.Ordinal));
        factory.Handler.Fail = true;
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        JsonElement history = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{profile}/watch-history");
        Assert.False(history.GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await kid.GetAsync("/api/v1/kid/home")).StatusCode);
    }

    [Fact]
    public async Task Reused_movie_and_episode_keys_do_not_match_a_different_guid_or_parent()
    {
        using WatchFactory factory = new();
        await factory.LinkPlexAsync();
        (_, int profile) = await factory.SignInKidAsync();
        await factory.SeedAsync(profile, "42", includeShow: true);
        factory.Handler.IncludeEpisode = true;
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        JsonElement initial = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{profile}/watch-history");
        Assert.Contains(initial.GetProperty("titles").EnumerateArray(), t => t.GetProperty("tmdbId").GetInt32() == 82728);
        factory.Plex.AddItem(new FakePlexServer.FakeItem("20111", "episode", "plex://episode/different", "Episode", 2) { GrandparentRatingKey = "201" });
        JsonElement wrongEpisode = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{profile}/watch-history");
        Assert.DoesNotContain(wrongEpisode.GetProperty("titles").EnumerateArray(), t => t.GetProperty("tmdbId").GetInt32() == 82728);
        factory.Plex.AddItem(new FakePlexServer.FakeItem("20111", "episode", "plex://episode/5d9c11ae2df347001e3b1a52", "Episode", 2) { GrandparentRatingKey = "201" });
        factory.Plex.Rematch("201", "plex://show/different", "Other show");
        JsonElement wrongShow = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{profile}/watch-history");
        Assert.DoesNotContain(wrongShow.GetProperty("titles").EnumerateArray(), t => t.GetProperty("tmdbId").GetInt32() == 82728);
        factory.Plex.Rematch("201", "plex://show/5d9c086c46115600200aa2fe", "Show");
        factory.Plex.Rematch("101", "plex://movie/different", "Other");
        factory.Plex.AddItem(new FakePlexServer.FakeItem("20111", "episode", "plex://episode/5d9c11ae2df347001e3b1a52", "Episode", 2) { GrandparentRatingKey = "999" });
        JsonElement after = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{profile}/watch-history");
        Assert.DoesNotContain(after.GetProperty("titles").EnumerateArray(), t => t.GetProperty("tmdbId").GetInt32() is 700 or 82728);
    }

    [Fact]
    public async Task Wrong_server_or_wrong_user_never_exposes_history()
    {
        using WatchFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int profile) = await factory.SignInKidAsync();
        await factory.SeedAsync(profile, "42");
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        factory.Handler.WrongServer = true;
        JsonElement wrongServer = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{profile}/watch-history");
        Assert.False(wrongServer.GetProperty("available").GetBoolean());
        factory.Handler.WrongServer = false;
        factory.Handler.WrongUser = true;
        JsonElement wrongUser = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/kid-profiles/{profile}/watch-history");
        Assert.False(wrongUser.GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await kid.GetAsync("/api/v1/kid/home")).StatusCode);
    }

    private sealed class WatchFactory : KidRequestApiFactory
    {
        public HistoryHandler Handler { get; } = new();

        public async Task SeedAsync(int profileId, string user, bool includeShow = false, bool seedIndex = true)
        {
            using IServiceScope scope = this.Services.CreateScope();
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            (await db.KidProfiles.SingleAsync(p => p.Id == profileId)).PlexUserId = user;
            if (seedIndex)
            {
                db.LibraryRefreshStates.Add(new LibraryRefreshState { ServerId = FakePlexServer.MachineIdentifier, LastFullRefreshAt = DateTimeOffset.UtcNow });
                foreach ((string key, int tmdb) in new[] { ("101", 700), ("102", 702) })
                {
                    db.LibraryItems.Add(new LibraryItem { ServerId = FakePlexServer.MachineIdentifier, RatingKey = key, Guid = this.Plex.Item(key).Guid, Kind = MediaKind.Movie, LibraryId = "1", Title = "Demo", CanonicalTitle = new LibraryTitle { Kind = MediaKind.Movie, TmdbId = tmdb } });
                }

                if (includeShow)
                {
                    db.LibraryItems.Add(new LibraryItem { ServerId = FakePlexServer.MachineIdentifier, RatingKey = "201", Guid = this.Plex.Item("201").Guid, Kind = MediaKind.Show, LibraryId = "2", Title = "Demo show", CanonicalTitle = new LibraryTitle { Kind = MediaKind.Show, TmdbId = 82728 } });
                }
            }

            await db.SaveChangesAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Tautulli:Url", "http://tautulli.example.test/");
            builder.UseSetting("Tautulli:ApiKey", "test-key");
            builder.ConfigureTestServices(services => services.AddHttpClient(TautulliHistory.ClientName).ConfigurePrimaryHttpMessageHandler(() => this.Handler));
        }
    }

    private sealed class HistoryHandler : HttpMessageHandler
    {
        public bool WrongServer { get; set; }
        public bool WrongUser { get; set; }
        public bool PartialOnly { get; set; }
        public bool IncludeEpisode { get; set; }
        public bool Paginated { get; set; }
        public bool SecondPageRequested { get; private set; }
        public bool Fail { get; set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests++;
            if (this.Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            Assert.True(request.Headers.Contains("X-Api-Key"));
            Assert.DoesNotContain("test-key", request.RequestUri!.Query);
            string query = request.RequestUri.Query;
            object[] rows;
            if (this.Paginated)
            {
                bool secondPage = query.Contains("start=100", StringComparison.Ordinal);
                this.SecondPageRequested |= secondPage;
                rows = secondPage
                    ? [new { user_id = 42, media_type = "movie", rating_key = 101, guid = "plex://movie/5d7768ba96b655001fdc0408", watched_status = 1m, percent_complete = 95, date = 100 }]
                    : [.. Enumerable.Range(200, 100).Reverse().Select(date => new { user_id = 42, media_type = "movie", rating_key = 101, guid = "plex://movie/5d7768ba96b655001fdc0408", watched_status = 0.25m, percent_complete = 35, date })];
            }
            else if (query.Contains("user_id=43", StringComparison.Ordinal))
            {
                rows = [new { user_id = 43, media_type = "movie", rating_key = 102, guid = "plex://movie/5d776b59ad5437001f79c6f8", watched_status = 1m, percent_complete = 95, date = 200 }];
            }
            else
            {
                rows = [
                    new { user_id = 42, media_type = "movie", rating_key = 102, guid = "plex://movie/5d776b59ad5437001f79c6f8", watched_status = 0.25m, percent_complete = 35, date = 200 },
                    new { user_id = this.WrongUser ? 43 : 42, media_type = "movie", rating_key = 101, guid = "plex://movie/5d7768ba96b655001fdc0408", watched_status = this.PartialOnly ? 0.75m : 1m, percent_complete = 95, date = 100 },
                ];
                if (this.IncludeEpisode)
                {
                    rows = [.. rows, new { user_id = 42, media_type = "episode", rating_key = 20111, grandparent_rating_key = 201, guid = "plex://episode/5d9c11ae2df347001e3b1a52", watched_status = 1m, percent_complete = 95, date = 50 }];
                }
            }

            string body = request.RequestUri.Query.Contains("get_server_info", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { response = new { result = "success", data = new { pms_identifier = this.WrongServer ? "wrong" : FakePlexServer.MachineIdentifier } } })
                : JsonSerializer.Serialize(new { response = new { result = "success", data = new { recordsFiltered = this.Paginated ? 101 : rows.Length, data = rows } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
