using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.System;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Integrations.Plex;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Arr;
using Wishlistarr.Tests.Plex;
using Wishlistarr.Tests.Seerr;
using Wishlistarr.Tests.Tmdb;

namespace Wishlistarr.Tests;

public sealed class SetupTests
{
    private const string WrongTmdbKey = "example-wrong-tmdb-key";
    private const string WrongServiceKey = "example-wrong-service-key";

    private static readonly string[] Secrets =
    [
        FakeTmdbServer.ValidApiKey,
        WrongTmdbKey,
        FakeArrServer.ApiKey,
        FakeSeerrServer.ApiKey,
        WrongServiceKey,
        FakePlexServer.AccountToken,
        FakePlexServer.ServerToken,
    ];

    [Theory]
    [InlineData("seerr")]
    [InlineData("arr")]
    public async Task Complete_rechecks_saved_connections_and_restrictions_and_persists_completion(string mode)
    {
        await using FakePlexServer plex = await FakePlexServer.StartAsync();
        await using FakeTmdbServer tmdb = await FakeTmdbServer.StartAsync();
        using SetupApiFactory factory = new(plex, tmdb);
        await using FakeSeerrServer seerr = await FakeSeerrServer.StartAsync();
        await using FakeArrServer sonarr = await FakeArrServer.StartAsync(ArrService.Sonarr);
        await using FakeArrServer radarr = await FakeArrServer.StartAsync(ArrService.Radarr);
        HttpClient client = await PrepareAsync(factory, seerr, sonarr, radarr, mode);
        Assert.False((await StatusAsync(client)).GetProperty("completed").GetBoolean());

        HttpResponseMessage completed = await client.PostAsJsonAsync("/api/v1/setup/complete", new { profileId = 1 });

        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.True((await StatusAsync(client)).GetProperty("completed").GetBoolean());
        Assert.False((await StatusAsync(client)).GetProperty("showGuide").GetBoolean());
        HttpClient later = await factory.CreateAuthorizedClientAsync();
        Assert.True((await StatusAsync(later)).GetProperty("completed").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await later.PostAsJsonAsync("/api/v1/setup/complete", new { profileId = 1 })).StatusCode);
        string settings = await client.GetStringAsync("/api/v1/settings");
        AssertNoSecrets(settings);
        Assert.DoesNotContain("system.setup_completed", settings, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("label=Demo", "label=Demo", "")]
    [InlineData("contentRating=G%2CPG", "contentRating=TV-Y%2CTV-G", "")]
    [InlineData("contentRating=PG|label=Demo", "contentRating=TV-Y|label=Demo", "")]
    [InlineData("contentRating!=R|label=Demo", "label=Demo", "")]
    [InlineData("", "", "contentRating=PG")]
    [InlineData("", "", "label=Demo")]
    [InlineData("label=Demo", "contentRating=TV-Y", null)]
    public async Task Rating_caps_and_allowed_profile_labels_both_pass_the_restriction_check(string movies, string television, string? all)
    {
        await using FakePlexServer plex = await FakePlexServer.StartAsync();
        await using FakeTmdbServer tmdb = await FakeTmdbServer.StartAsync();
        using SetupApiFactory factory = new(plex, tmdb);
        await using FakeSeerrServer seerr = await FakeSeerrServer.StartAsync();
        await using FakeArrServer sonarr = await FakeArrServer.StartAsync(ArrService.Sonarr);
        await using FakeArrServer radarr = await FakeArrServer.StartAsync(ArrService.Radarr);
        HttpClient client = await PrepareAsync(factory, seerr, sonarr, radarr, "seerr");
        factory.Plex.UsersXml = UsersXml(movies, television, all);

        HttpResponseMessage completed = await client.PostAsJsonAsync("/api/v1/setup/complete", new { profileId = 1 });

        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.True((await StatusAsync(client)).GetProperty("completed").GetBoolean());
    }

    [Theory]
    [InlineData("plex")]
    [InlineData("tmdb")]
    [InlineData("mode")]
    [InlineData("seerr")]
    [InlineData("profile")]
    [InlineData("unmapped")]
    [InlineData("open")]
    [InlineData("unknown")]
    [InlineData("moviesOnly")]
    [InlineData("excludedRatingOnly")]
    [InlineData("emptyRating")]
    [InlineData("otherLabel")]
    [InlineData("excludedOwnLabel")]
    [InlineData("unreadableFilter")]
    [InlineData("unknownRule")]
    [InlineData("quality")]
    [InlineData("folder")]
    [InlineData("radarr")]
    public async Task Incomplete_or_unreachable_step_never_completes_setup(string failure)
    {
        await using FakePlexServer plex = await FakePlexServer.StartAsync();
        await using FakeTmdbServer tmdb = await FakeTmdbServer.StartAsync();
        using SetupApiFactory factory = new(plex, tmdb);
        await using FakeSeerrServer seerr = await FakeSeerrServer.StartAsync();
        await using FakeArrServer sonarr = await FakeArrServer.StartAsync(ArrService.Sonarr);
        await using FakeArrServer radarr = await FakeArrServer.StartAsync(ArrService.Radarr);
        bool arr = failure is "quality" or "folder" or "radarr";
        HttpClient client = await PrepareAsync(factory, seerr, sonarr, radarr, arr ? "arr" : "seerr");
        string? restrictionFilters = failure switch
        {
            "open" => UsersXml(string.Empty, string.Empty, string.Empty),
            "unknown" => "<html>Unavailable</html>",
            "moviesOnly" => UsersXml("label=Demo", string.Empty, string.Empty),
            "excludedRatingOnly" => UsersXml("contentRating!=R", "contentRating!=TV-MA", string.Empty),
            "emptyRating" => UsersXml("contentRating=", "contentRating=", string.Empty),
            "otherLabel" => UsersXml("label=Other", "label=Other", string.Empty),
            "excludedOwnLabel" => UsersXml("label!=Demo", "label!=Demo", string.Empty),
            "unreadableFilter" => UsersXml("label=Demo|unknown", "label=Demo", string.Empty),
            "unknownRule" => UsersXml("contentRating=PG|unknown=rule", "label=Demo", string.Empty),
            _ => null,
        };
        if (restrictionFilters is not null)
        {
            factory.Plex.UsersXml = restrictionFilters;
        }

        switch (failure)
        {
            case "plex":
                await client.DeleteAsync("/api/v1/plex/link");
                break;
            case "tmdb":
                await client.PutAsJsonAsync("/api/v1/settings/tmdb.api_key", new { value = WrongTmdbKey });
                break;
            case "mode":
                await client.PutAsJsonAsync("/api/v1/settings/fulfilment.mode", new { value = "none" });
                break;
            case "seerr":
                await client.PutAsJsonAsync("/api/v1/settings/seerr", new { url = seerr.Address.ToString(), apiKey = WrongServiceKey, userId = FakeSeerrServer.DefaultUserId });
                break;
            case "unmapped":
                await client.PutAsJsonAsync("/api/v1/kid-profiles/1", new { name = "Demo", avatar = "fox", plexLabel = "Demo" });
                break;
            case "quality":
            case "folder":
                await client.PutAsJsonAsync("/api/v1/settings/sonarr", new { url = sonarr.Address.ToString(), qualityProfileId = failure == "quality" ? 999 : 4, rootFolderPath = failure == "folder" ? "/missing" : "/media/kids" });
                break;
            case "radarr":
                await client.DeleteAsync("/api/v1/settings/radarr");
                break;
        }

        HttpResponseMessage completed = await client.PostAsJsonAsync("/api/v1/setup/complete", new { profileId = failure == "profile" ? 999 : 1 });

        Assert.Contains(completed.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.BadGateway });
        Assert.False((await StatusAsync(client)).GetProperty("completed").GetBoolean());
        string body = await completed.Content.ReadAsStringAsync();
        JsonElement problem = JsonDocument.Parse(body).RootElement;
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        if (restrictionFilters is not null)
        {
            Assert.Equal(HttpStatusCode.Conflict, completed.StatusCode);
            Assert.StartsWith("Plex restrictions are not confirmed", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        }

        AssertNoSecrets(body);
        AssertNoSecrets(Logs(factory));
    }

    [Fact]
    public async Task An_unparseable_upstream_reply_is_a_friendly_bad_gateway_problem()
    {
        await using FakePlexServer plex = await FakePlexServer.StartAsync();
        await using FakeTmdbServer tmdb = await FakeTmdbServer.StartAsync();
        using SetupApiFactory factory = new(plex, tmdb) { Seerr = new UnparseableSeerrBackend() };
        await using FakeSeerrServer seerr = await FakeSeerrServer.StartAsync();
        await using FakeArrServer sonarr = await FakeArrServer.StartAsync(ArrService.Sonarr);
        await using FakeArrServer radarr = await FakeArrServer.StartAsync(ArrService.Radarr);
        HttpClient client = await PrepareAsync(factory, seerr, sonarr, radarr, "seerr");

        HttpResponseMessage completed = await client.PostAsJsonAsync("/api/v1/setup/complete", new { profileId = 1 });

        Assert.Equal(HttpStatusCode.BadGateway, completed.StatusCode);
        JsonElement problem = await completed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Request service could not be checked. Check that step and try again.", problem.GetProperty("title").GetString());
        Assert.False((await StatusAsync(client)).GetProperty("completed").GetBoolean());
    }

    [Fact]
    public async Task The_guide_opens_on_its_own_only_until_it_is_exited_or_connections_are_saved()
    {
        using ApiFactory fresh = new();
        HttpClient parent = await fresh.CreateAuthorizedClientAsync();
        JsonElement first = await StatusAsync(parent);
        Assert.False(first.GetProperty("completed").GetBoolean());
        Assert.True(first.GetProperty("showGuide").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsync("/api/v1/setup/dismiss", null)).StatusCode);

        JsonElement later = await StatusAsync(await fresh.CreateAuthorizedClientAsync());
        Assert.False(later.GetProperty("completed").GetBoolean());
        Assert.False(later.GetProperty("showGuide").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PutAsJsonAsync("/api/v1/settings/system.setup_dismissed", new { value = "false" })).StatusCode);

        await using FakePlexServer plex = await FakePlexServer.StartAsync();
        await using FakeTmdbServer tmdb = await FakeTmdbServer.StartAsync();
        using SetupApiFactory configured = new(plex, tmdb);
        await using FakeSeerrServer seerr = await FakeSeerrServer.StartAsync();
        await using FakeArrServer sonarr = await FakeArrServer.StartAsync(ArrService.Sonarr);
        await using FakeArrServer radarr = await FakeArrServer.StartAsync(ArrService.Radarr);
        HttpClient client = await PrepareAsync(configured, seerr, sonarr, radarr, "seerr");
        JsonElement existing = await StatusAsync(client);
        Assert.False(existing.GetProperty("completed").GetBoolean());
        Assert.False(existing.GetProperty("showGuide").GetBoolean());

        (await client.PutAsJsonAsync("/api/v1/settings/fulfilment.mode", new { value = "arr" })).EnsureSuccessStatusCode();
        Assert.False((await StatusAsync(client)).GetProperty("showGuide").GetBoolean());
        await client.DeleteAsync("/api/v1/settings/radarr");
        Assert.True((await StatusAsync(client)).GetProperty("showGuide").GetBoolean());
    }

    [Fact]
    public async Task Setup_changes_are_refused_without_the_antiforgery_header_for_a_signed_in_browser()
    {
        using AuthApiFactory factory = new();
        await factory.EnsureParentAsync();
        HttpClient browser = factory.CreateClient();
        HttpResponseMessage login = await browser.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        foreach (string path in new[] { "/api/v1/setup/complete", "/api/v1/setup/dismiss", "/api/v1/settings/tmdb/test-candidate", "/api/v1/settings/seerr/test-candidate", "/api/v1/settings/sonarr/test-candidate" })
        {
            HttpResponseMessage refused = await browser.PostAsJsonAsync(path, new { profileId = 1, apiKey = WrongTmdbKey });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("antiforgery", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        JsonElement status = await StatusAsync(browser);
        Assert.False(status.GetProperty("completed").GetBoolean());
        Assert.True(status.GetProperty("showGuide").GetBoolean());
    }

    [Fact]
    public async Task Candidate_tests_check_unsaved_values_without_saving_or_echoing_them()
    {
        await using FakePlexServer plex = await FakePlexServer.StartAsync();
        await using FakeTmdbServer tmdb = await FakeTmdbServer.StartAsync();
        using SetupApiFactory factory = new(plex, tmdb);
        await using FakeSeerrServer seerr = await FakeSeerrServer.StartAsync();
        await using FakeSeerrServer otherSeerr = await FakeSeerrServer.StartAsync();
        await using FakeArrServer sonarr = await FakeArrServer.StartAsync(ArrService.Sonarr);
        await using FakeArrServer radarr = await FakeArrServer.StartAsync(ArrService.Radarr);
        HttpClient client = await PrepareAsync(factory, seerr, sonarr, radarr, "seerr");
        List<string> bodies = [];

        async Task<HttpResponseMessage> PostAsync(string path, object body)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync(path, body);
            bodies.Add(await response.Content.ReadAsStringAsync());
            return response;
        }

        async Task<bool> ValidAsync(HttpResponseMessage response) =>
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("valid").GetBoolean();

        Assert.False(await ValidAsync(await PostAsync("/api/v1/settings/tmdb/test-candidate", new { apiKey = WrongTmdbKey })));
        Assert.True(await ValidAsync(await PostAsync("/api/v1/settings/tmdb/test-candidate", new { apiKey = FakeTmdbServer.ValidApiKey })));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/v1/settings/tmdb/test-candidate", new { apiKey = "bad key" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/v1/settings/tmdb/test-candidate", new { })).StatusCode);

        HttpResponseMessage seerrWrong = await PostAsync("/api/v1/settings/seerr/test-candidate", new { url = seerr.Address.ToString(), apiKey = WrongServiceKey, userId = FakeSeerrServer.DefaultUserId });
        Assert.Equal(HttpStatusCode.BadGateway, seerrWrong.StatusCode);
        HttpResponseMessage seerrSaved = await PostAsync("/api/v1/settings/seerr/test-candidate", new { url = seerr.Address.ToString(), userId = FakeSeerrServer.DefaultUserId });
        Assert.Equal(HttpStatusCode.OK, seerrSaved.StatusCode);
        HttpResponseMessage seerrMoved = await PostAsync("/api/v1/settings/seerr/test-candidate", new { url = otherSeerr.Address.ToString(), userId = FakeSeerrServer.DefaultUserId });
        Assert.Equal(HttpStatusCode.BadRequest, seerrMoved.StatusCode);
        Assert.Empty(otherSeerr.Requests);

        Assert.False(await ValidAsync(await PostAsync("/api/v1/settings/sonarr/test-candidate", new { url = sonarr.Address.ToString(), apiKey = WrongServiceKey })));
        Assert.True(await ValidAsync(await PostAsync("/api/v1/settings/sonarr/test-candidate", new { url = sonarr.Address.ToString() })));
        int radarrCalls = radarr.Requests.Count;
        HttpResponseMessage sonarrMoved = await PostAsync("/api/v1/settings/sonarr/test-candidate", new { url = radarr.Address.ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, sonarrMoved.StatusCode);
        Assert.Equal(radarrCalls, radarr.Requests.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/v1/settings/sonarr/test-candidate", new { url = sonarr.Address.ToString(), apiKey = "bad\u0001key" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/setup/complete", new { profileId = 1 })).StatusCode);
        Assert.All(bodies, AssertNoSecrets);
        AssertNoSecrets(Logs(factory));
    }

    [Fact]
    public async Task Setup_is_parent_only_and_its_completion_marker_cannot_be_set_through_settings()
    {
        using ApiFactory factory = new();
        HttpClient anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/setup/complete", new { profileId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/setup/dismiss", null)).StatusCode);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PutAsJsonAsync("/api/v1/settings/system.setup_completed", new { value = "true" })).StatusCode);
        Assert.False((await StatusAsync(parent)).GetProperty("completed").GetBoolean());
    }

    private static void AssertNoSecrets(string text)
    {
        foreach (string secret in Secrets)
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    private static string Logs(ApiFactory factory) =>
        string.Join('\n', factory.Services.GetRequiredService<LogBuffer>().List(null).Select(entry => entry.Message));

    private static string UsersXml(string movies, string television, string? all)
    {
        string shared = all is null ? string.Empty : $" filterAll=\"{all}\"";
        return $"<MediaContainer><User id=\"5550001\"{shared} filterMovies=\"{movies}\" filterTelevision=\"{television}\" /></MediaContainer>";
    }

    private static Task<JsonElement> StatusAsync(HttpClient client) => client.GetFromJsonAsync<JsonElement>("/api/v1/setup");

    private static async Task<HttpClient> PrepareAsync(SetupApiFactory factory, FakeSeerrServer seerr, FakeArrServer sonarr, FakeArrServer radarr, string mode)
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        JsonElement link = await (await client.PostAsync("/api/v1/plex/link", null)).Content.ReadFromJsonAsync<JsonElement>();
        factory.Plex.PinApproved = true;
        (await client.PostAsync($"/api/v1/plex/link/{link.GetProperty("id").GetString()}/check", null)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/v1/plex/server", new { serverId = FakePlexServer.MachineIdentifier })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/v1/settings/tmdb.api_key", new { value = FakeTmdbServer.ValidApiKey })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/v1/settings/seerr", new { url = seerr.Address.ToString(), apiKey = FakeSeerrServer.ApiKey, userId = FakeSeerrServer.DefaultUserId })).EnsureSuccessStatusCode();
        foreach ((string service, FakeArrServer fake) in new[] { ("sonarr", sonarr), ("radarr", radarr) })
        {
            (await client.PutAsJsonAsync($"/api/v1/settings/{service}", new { url = fake.Address.ToString(), apiKey = FakeArrServer.ApiKey, qualityProfileId = 4, rootFolderPath = "/media/kids" })).EnsureSuccessStatusCode();
        }

        (await client.PutAsJsonAsync("/api/v1/settings/fulfilment.mode", new { value = mode })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/kid-profiles", new { name = "Demo", avatar = "fox", plexLabel = "Demo", plexUserId = "5550001" })).EnsureSuccessStatusCode();
        return client;
    }

    private sealed class SetupApiFactory(FakePlexServer plex, FakeTmdbServer tmdb) : ApiFactory
    {
        public FakePlexServer Plex { get; } = plex;

        public FakeTmdbServer Tmdb { get; } = tmdb;

        public ISeerrBackend? Seerr { get; init; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            this.Plex.UsersXml = UsersXml("label=Demo", "label=Demo", string.Empty);
            builder.ConfigureTestServices(services =>
            {
                if (this.Seerr is not null)
                {
                    services.AddSingleton(this.Seerr);
                }

                services.Configure<PlexOptions>(options =>
                {
                    options.PlexTvBaseAddress = this.Plex.Address;
                    options.RestrictionCacheLifetime = TimeSpan.Zero;
                });
                services.Configure<TmdbOptions>(options => options.ApiBaseAddress = this.Tmdb.ApiBaseAddress);
            });
        }
    }

    private sealed class UnparseableSeerrBackend : ISeerrBackend
    {
        public Task<SeerrTestResult> TestAsync(CancellationToken ct = default) =>
            throw new JsonException("The reply was not JSON.");

        public Task<SeerrTestResult> TestAsync(SeerrConnection connection, CancellationToken ct = default) =>
            throw new JsonException("The reply was not JSON.");

        public Task<SeerrSubmission> SubmitAsync(SeerrSubmitRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SeerrTrackedRequest> GetRequestAsync(int requestId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
