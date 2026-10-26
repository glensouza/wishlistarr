using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Settings;
using Wishlistarr.Integrations.Plex;

namespace Wishlistarr.Tests.Plex;

public sealed class PlexRestrictionTests
{
    private const string Enforced = "5550001";
    private const string Excluding = "5550002";
    private const string Open = "5550003";
    private const string OnlyMovies = "5550004";

    [Fact]
    public async Task Profile_list_shows_whether_the_mapped_user_enforces_the_label()
    {
        using PlexApiFactory factory = new();
        factory.Fake.UsersXml = Users();
        HttpClient client = await LinkedClientAsync(factory);
        await CreateProfileAsync(client, "Ada", Enforced);
        await CreateProfileAsync(client, "Ben", Excluding);
        await CreateProfileAsync(client, "Cy", Open);
        await CreateProfileAsync(client, "Di", OnlyMovies);
        await CreateProfileAsync(client, "Eli", null);

        Dictionary<string, JsonElement> profiles = await ListAsync(client);

        AssertRestrictions(profiles["Ada"], "enforced", "allowOnly", "enforced", "allowOnly");
        AssertRestrictions(profiles["Ben"], "notEnforced", null, "notEnforced", null);
        AssertRestrictions(profiles["Cy"], "notEnforced", null, "notEnforced", null);
        AssertRestrictions(profiles["Di"], "enforced", "allowOnly", "notEnforced", null);
        Assert.Equal(JsonValueKind.Null, profiles["Eli"].GetProperty("plexRestrictions").ValueKind);
    }

    [Fact]
    public async Task Unexpected_plex_responses_leave_the_state_unknown_without_failing()
    {
        using PlexApiFactory factory = new();
        HttpClient client = await LinkedClientAsync(factory);
        await CreateProfileAsync(client, "Ada", Enforced);
        string[] responses =
        [
            "<html><body>maintenance</body></html>",
            "<MediaContainer><Oops/></MediaContainer>",
            "this is not xml",
            "<MediaContainer><User id=\"5550001\" title=\"X\" restricted=\"1\"/></MediaContainer>",
            "<MediaContainer><User id=\"5559999\" filterMovies=\"\" filterTelevision=\"\"/></MediaContainer>",
        ];

        foreach (string body in responses)
        {
            factory.Fake.UsersXml = body;
            HttpResponseMessage response = await client.GetAsync("/api/v1/kid-profiles");
            Assert.True(response.IsSuccessStatusCode, body);
            JsonElement profile = (await response.Content.ReadFromJsonAsync<List<JsonElement>>())!.Single();
            AssertRestrictions(profile, "unknown", null, "unknown", null);
        }

        factory.Fake.UsersXml = Users();
        factory.Fake.UsersStatus = 500;
        HttpResponseMessage failed = await client.GetAsync("/api/v1/kid-profiles");
        JsonElement afterFailure = (await failed.Content.ReadFromJsonAsync<List<JsonElement>>())!.Single();
        Assert.True(failed.IsSuccessStatusCode);
        AssertRestrictions(afterFailure, "unknown", null, "unknown", null);
    }

    [Fact]
    public async Task Profiles_are_unknown_when_plex_is_not_linked()
    {
        using PlexApiFactory factory = new();
        factory.Fake.UsersXml = Users();
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        await CreateProfileAsync(client, "Ada", Enforced);

        JsonElement profile = (await client.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid-profiles"))!.Single();

        AssertRestrictions(profile, "unknown", null, "unknown", null);
    }

    [Fact]
    public async Task System_status_warns_about_each_profile_that_does_not_enforce_its_label()
    {
        using PlexApiFactory factory = new();
        factory.Fake.UsersXml = Users();
        HttpClient client = await LinkedClientAsync(factory);
        await CreateProfileAsync(client, "Ada", Enforced);
        await CreateProfileAsync(client, "Ben", Excluding);
        await CreateProfileAsync(client, "Cy", Open);
        await CreateProfileAsync(client, "Di", OnlyMovies);

        JsonElement status = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status");

        List<JsonElement> warnings = [.. status.GetProperty("warnings").EnumerateArray()];
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, warning => Assert.Equal("plex.label.not_enforced", warning.GetProperty("code").GetString()));
        Assert.Contains("movies and TV shows", MessageFor(warnings, "Ben"), StringComparison.Ordinal);
        string cy = MessageFor(warnings, "Cy");
        string di = MessageFor(warnings, "Di");
        Assert.Contains("movies and TV shows", cy, StringComparison.Ordinal);
        Assert.Contains("TV shows", di, StringComparison.Ordinal);
        Assert.DoesNotContain("Exclude", cy, StringComparison.Ordinal);
        Assert.Contains("Allow only", cy, StringComparison.Ordinal);
        Assert.DoesNotContain("movies", di, StringComparison.Ordinal);
    }

    [Fact]
    public async Task System_status_does_not_warn_when_the_state_is_unknown_or_everything_is_enforced()
    {
        using PlexApiFactory factory = new();
        factory.Fake.UsersXml = Users();
        HttpClient client = await LinkedClientAsync(factory);
        await CreateProfileAsync(client, "Ada", Enforced);

        JsonElement enforced = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status");
        factory.Fake.UsersXml = "not xml";
        JsonElement unknown = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status");

        Assert.Equal(0, enforced.GetProperty("warnings").GetArrayLength());
        Assert.Equal(0, unknown.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task Plex_is_asked_once_per_profile_list_however_many_profiles_are_mapped()
    {
        using PlexApiFactory factory = new();
        factory.Fake.UsersXml = Users();
        HttpClient client = await LinkedClientAsync(factory);
        await CreateProfileAsync(client, "Ada", Enforced);
        await CreateProfileAsync(client, "Ben", Excluding);
        await CreateProfileAsync(client, "Cy", Open);
        int before = factory.Fake.Requests.Count(request => request.Path == "/api/users");

        await client.GetAsync("/api/v1/kid-profiles");

        Assert.Equal(before + 1, factory.Fake.Requests.Count(request => request.Path == "/api/users"));
    }

    [Fact]
    public async Task Plex_is_asked_once_for_requests_within_the_cache_window()
    {
        using PlexApiFactory factory = new() { RestrictionCacheLifetime = TimeSpan.FromMinutes(1) };
        factory.Fake.UsersXml = Users();
        HttpClient client = await LinkedClientAsync(factory);
        await CreateProfileAsync(client, "Ada", Enforced);

        await client.GetAsync("/api/v1/kid-profiles");
        await client.GetAsync("/api/v1/system/status");

        Assert.Equal(1, factory.Fake.Requests.Count(request => request.Path == "/api/users"));
    }

    [Fact]
    public async Task A_slow_plex_answer_leaves_the_state_unknown()
    {
        using PlexApiFactory factory = new() { RestrictionTimeout = TimeSpan.FromMilliseconds(100) };
        factory.Fake.UsersXml = Users();
        HttpClient client = await LinkedClientAsync(factory);
        await CreateProfileAsync(client, "Ada", Enforced);
        factory.Fake.Delay = request => request.Path == "/api/users" ? Task.Delay(TimeSpan.FromSeconds(5)) : Task.CompletedTask;

        HttpResponseMessage response = await client.GetAsync("/api/v1/kid-profiles");

        Assert.True(response.IsSuccessStatusCode);
        AssertRestrictions((await response.Content.ReadFromJsonAsync<List<JsonElement>>())!.Single(), "unknown", null, "unknown", null);
    }

    [Fact]
    public async Task Reader_sends_the_account_sign_in_and_returns_filters_by_user_id()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        harness.Server.UsersXml = Users();
        await harness.LinkAsync();
        IMediaServerRestrictions reader = (IMediaServerRestrictions)harness.Provider.GetService(typeof(IMediaServerRestrictions))!;

        IReadOnlyDictionary<string, UserContentFilters>? filters = await reader.GetUserFiltersAsync();

        Assert.NotNull(filters);
        Assert.Equal("contentRating=TV-Y%2CTV-Y7|label=Ada", filters[Enforced].Movies);
        Assert.Equal(string.Empty, filters[Open].All);
        Assert.Contains(harness.Server.Requests, request => request.Path == "/api/users" && request.Token == FakePlexServer.AccountToken);
    }

    [Fact]
    public async Task Reader_returns_nothing_when_settings_cannot_be_read()
    {
        ServiceCollection services = new();
        services.AddPlex<FailingSettings>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        IReadOnlyDictionary<string, UserContentFilters>? filters = await provider.GetRequiredService<IMediaServerRestrictions>().GetUserFiltersAsync();

        Assert.Null(filters);
    }

    [Fact]
    public async Task Plex_health_is_ok_when_the_mapped_user_enforces_the_label()
    {
        using PlexApiFactory factory = new();
        factory.Fake.UsersXml = Users();
        HttpClient client = await LinkedClientAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/plex/server", new { serverId = FakePlexServer.MachineIdentifier })).StatusCode);
        await CreateProfileAsync(client, "Ada", Enforced);

        JsonElement plex = await PlexCheckAsync(client);

        Assert.Equal("ok", plex.GetProperty("status").GetString());
        Assert.Contains("Connected", plex.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(plex.GetProperty("fix").GetString()));
    }

    [Fact]
    public async Task Plex_health_warns_when_the_label_is_not_enforced_and_the_public_endpoint_stays_plain()
    {
        using PlexApiFactory factory = new();
        factory.Fake.UsersXml = Users();
        HttpClient client = await LinkedClientAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/plex/server", new { serverId = FakePlexServer.MachineIdentifier })).StatusCode);
        await CreateProfileAsync(client, "Cy", Open);

        JsonElement plex = await PlexCheckAsync(client);
        string publicHealth = await factory.CreateClient().GetStringAsync("/api/health");

        Assert.Equal("warning", plex.GetProperty("status").GetString());
        Assert.Contains("Cy", plex.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("Allow only", plex.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(plex.GetProperty("fix").GetString()));
        Assert.DoesNotContain("Cy", publicHealth, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> PlexCheckAsync(HttpClient client)
    {
        using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync("/api/v1/system/health"));
        return document.RootElement.EnumerateArray().Single(item => item.GetProperty("integration").GetString() == "plex").Clone();
    }

    private static string MessageFor(List<JsonElement> warnings, string name) =>
        warnings.Single(warning => warning.GetProperty("message").GetString()!.Contains(name, StringComparison.Ordinal))
            .GetProperty("message").GetString()!;

    private static string Users() =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <MediaContainer friendlyName="myPlex" size="4">
          <User id="{Enforced}" title="Fixture Ada" home="1" restricted="1" filterAll="" filterMovies="contentRating=TV-Y%2CTV-Y7|label=Ada" filterMusic="" filterPhotos="" filterTelevision="label=Ada" />
          <User id="{Excluding}" title="Fixture Ben" home="1" restricted="1" filterAll="" filterMovies="label!=Ben" filterMusic="" filterPhotos="" filterTelevision="contentRating!=TV-MA|label!=Ben" />
          <User id="{Open}" title="Fixture Cy" home="0" restricted="0" filterAll="" filterMovies="" filterMusic="" filterPhotos="" filterTelevision="" />
          <User id="{OnlyMovies}" title="Fixture Di" home="1" restricted="1" filterAll="" filterMovies="label=Di" filterMusic="" filterPhotos="" filterTelevision="label=Other" />
        </MediaContainer>
        """;

    private static async Task<HttpClient> LinkedClientAsync(PlexApiFactory factory)
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        JsonElement link = await (await client.PostAsync("/api/v1/plex/link", null)).Content.ReadFromJsonAsync<JsonElement>();
        factory.Fake.PinApproved = true;
        await client.PostAsync($"/api/v1/plex/link/{link.GetProperty("id").GetString()}/check", null);
        return client;
    }

    private static async Task CreateProfileAsync(HttpClient client, string name, string? plexUserId)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name, avatar = "fox", plexUserId });
        Assert.True(response.IsSuccessStatusCode);
    }

    private static async Task<Dictionary<string, JsonElement>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid-profiles"))!
            .ToDictionary(profile => profile.GetProperty("name").GetString()!);

    private static void AssertRestrictions(JsonElement profile, string movies, string? movieMode, string television, string? tvMode)
    {
        JsonElement restrictions = profile.GetProperty("plexRestrictions");
        Assert.Equal(movies, restrictions.GetProperty("movies").GetProperty("state").GetString());
        Assert.Equal(movieMode, Mode(restrictions.GetProperty("movies")));
        Assert.Equal(television, restrictions.GetProperty("television").GetProperty("state").GetString());
        Assert.Equal(tvMode, Mode(restrictions.GetProperty("television")));
    }

    private static string? Mode(JsonElement restriction) =>
        restriction.TryGetProperty("mode", out JsonElement mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
}

internal sealed class FailingSettings : IAppSettings
{
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => throw new InvalidOperationException("Settings are unavailable.");

    public Task<IReadOnlyDictionary<string, string?>> GetAsync(IReadOnlyList<string> keys, CancellationToken ct = default) =>
        throw new InvalidOperationException("Settings are unavailable.");

    public Task SetAsync(string key, string value, bool isSecret, CancellationToken ct = default) => throw new InvalidOperationException("Settings are unavailable.");

    public Task SetAsync(IReadOnlyList<AppSettingChange> changes, CancellationToken ct = default) => throw new InvalidOperationException("Settings are unavailable.");

    public Task DeleteAsync(string key, CancellationToken ct = default) => throw new InvalidOperationException("Settings are unavailable.");
}
