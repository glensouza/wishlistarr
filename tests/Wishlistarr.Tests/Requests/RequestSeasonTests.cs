using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Arr;

namespace Wishlistarr.Tests.Requests;

public sealed class RequestSeasonTests
{
    [Fact]
    public async Task Partial_and_missing_seasons_are_reported_separately_with_show_level_access()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _) = await PrepareAsync(factory);
        HttpResponseMessage response = await AskAsync(kid, [1, 2]);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await CheckAsync(factory);

        JsonElement request = Assert.Single((await kid.GetFromJsonAsync<JsonElement>(KidRequestApiFactory.RequestsPath)).EnumerateArray());
        Assert.Equal("monitoring", request.GetProperty("state").GetString());
        JsonElement[] seasons = [.. request.GetProperty("seasons").EnumerateArray()];
        Assert.Equal([1, 2], seasons.Select(season => season.GetProperty("number").GetInt32()));
        Assert.Equal(["partial", "missing"], seasons.Select(season => season.GetProperty("availability").GetString()));
        Assert.Equal(1, seasons[0].GetProperty("availableEpisodes").GetInt32());
        Assert.True(seasons[0].GetProperty("requested").GetBoolean());
        Assert.True(seasons[1].GetProperty("requested").GetBoolean());
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
        Assert.Empty(factory.Plex.Item("2011").Labels);

        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        JsonElement row = Assert.Single((await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests?state=labeled&includeMonitoring=true")).GetProperty("items").EnumerateArray());
        Assert.True(row.GetProperty("canRevoke").GetBoolean());
        Assert.Equal("partial", row.GetProperty("seasons")[0].GetProperty("availability").GetString());
    }

    [Fact]
    public async Task Ongoing_shows_keep_monitoring_complete_seasons_and_dispatch_a_new_season_once()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _) = await PrepareAsync(factory, FakeArrServer.ShowTmdbId);
        await factory.ConfiguredParentAsync("arr");
        factory.Plex.Item("2011").LeafCount = 10;
        await AskAsync(kid, [1], FakeArrServer.ShowTmdbId);
        await CheckAsync(factory);
        MediaRequest original = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Monitoring, original.State);
        Assert.Empty(factory.Sonarr.Library);
        int writes = factory.LabelWrites;

        SetDetails(factory, "Returning Series", includeThird: true, tmdbId: FakeArrServer.ShowTmdbId);
        await CheckAsync(factory);
        HttpResponseMessage added = await AskAsync(kid, [3], FakeArrServer.ShowTmdbId);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        JsonElement body = await added.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(original.Id, body.GetProperty("id").GetInt32());
        Assert.Equal("monitoring", body.GetProperty("state").GetString());
        Assert.True(body.GetProperty("seasons").EnumerateArray().Single(season => season.GetProperty("number").GetInt32() == 3).GetProperty("requested").GetBoolean());
        await WaitForDispatchJobAsync(factory);
        MediaRequest delivered = Assert.Single(await factory.GetRequestsAsync());
        Assert.NotNull(delivered.DispatchedAt);
        Assert.Equal(RequestState.Monitoring, delivered.State);
        Assert.Equal([1, 3], MonitoredSeasons(factory));
        int backendWrites = BackendWrites(factory);

        await AskAsync(kid, [3], FakeArrServer.ShowTmdbId);
        await DispatchAsync(factory);
        Assert.Equal(backendWrites, BackendWrites(factory));
        Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(writes, factory.LabelWrites);
        Assert.Single(await factory.GetHistoryAsync(original.Id), item => item.ToState == RequestState.Approved);
    }

    [Fact]
    public async Task A_new_season_stays_unsent_until_the_stored_sonarr_series_lists_every_selection()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _) = await PrepareAsync(factory, FakeArrServer.ShowTmdbId);
        await factory.ConfiguredParentAsync("arr");
        factory.Plex.Item("2011").LeafCount = 10;
        await AskAsync(kid, [1], FakeArrServer.ShowTmdbId);
        await CheckAsync(factory);
        JsonObject stored = factory.Sonarr.SeedSeries(monitored: true, 1);
        stored["seasons"]!.AsArray().RemoveAt(3);
        SetDetails(factory, "Returning Series", includeThird: true, tmdbId: FakeArrServer.ShowTmdbId);

        await AskAsync(kid, [3], FakeArrServer.ShowTmdbId);
        await WaitForDispatchJobAsync(factory);
        MediaRequest waiting = Assert.Single(await factory.GetRequestsAsync());
        Assert.Null(waiting.DispatchedAt);
        Assert.Equal(RequestState.Monitoring, waiting.State);
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
        Assert.Empty(factory.Sonarr.Sent("POST", "/api/v3/command"));
        Assert.Equal(0, BackendWrites(factory));

        stored["seasons"]!.AsArray().Add(new JsonObject { ["seasonNumber"] = 3, ["monitored"] = false });
        await DispatchAsync(factory);
        MediaRequest delivered = Assert.Single(await factory.GetRequestsAsync());
        Assert.NotNull(delivered.DispatchedAt);
        Assert.Equal(RequestState.Monitoring, delivered.State);
        Assert.Equal([1, 3], MonitoredSeasons(factory));
        Assert.Equal([1, 3], factory.Sonarr.Sent("POST", "/api/v3/command").Select(search => search.Body!["seasonNumber"]!.GetValue<int>()));
        int writes = BackendWrites(factory);
        await DispatchAsync(factory);
        Assert.Equal(writes, BackendWrites(factory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_kid_can_turn_parent_granted_show_access_into_a_download_request(bool oneSeason)
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, int profileId) = await PrepareAsync(factory, FakeArrServer.ShowTmdbId);
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        HttpResponseMessage grant = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = profileId, kind = "show", tmdbId = FakeArrServer.ShowTmdbId });
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        await CheckAsync(factory);
        await DispatchAsync(factory);
        MediaRequest access = Assert.Single(await factory.GetRequestsAsync());
        Assert.False(access.DownloadRequested);
        Assert.Empty(factory.Sonarr.Library);
        int labelWrites = factory.LabelWrites;

        HttpResponseMessage asked = await AskAsync(kid, oneSeason ? [2] : null, FakeArrServer.ShowTmdbId);
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        await WaitForDispatchJobAsync(factory);
        MediaRequest downloaded = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(access.Id, downloaded.Id);
        Assert.True(downloaded.DownloadRequested);
        Assert.Equal(!oneSeason, downloaded.AllSeasons);
        Assert.NotNull(downloaded.DispatchedAt);
        Assert.Equal(RequestState.Monitoring, downloaded.State);
        int[] expected = oneSeason ? [2] : [1, 2, 3];
        Assert.Equal(expected, MonitoredSeasons(factory));
        Assert.Equal(labelWrites, factory.LabelWrites);
        int writes = BackendWrites(factory);
        await AskAsync(kid, oneSeason ? [2] : null, FakeArrServer.ShowTmdbId);
        await DispatchAsync(factory);
        Assert.Equal(writes, BackendWrites(factory));
    }

    [Fact]
    public async Task An_ended_show_finishes_only_when_the_requested_season_is_complete()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _) = await PrepareAsync(factory);
        SetDetails(factory, "Ended");
        await AskAsync(kid, [1]);
        await CheckAsync(factory);
        Assert.Equal(RequestState.Monitoring, Assert.Single(await factory.GetRequestsAsync()).State);
        factory.Plex.Item("2011").LeafCount = 10;
        await CheckAsync(factory);
        Assert.Equal(RequestState.Labeled, Assert.Single(await factory.GetRequestsAsync()).State);
    }

    [Fact]
    public async Task A_season_request_cannot_restore_a_revoked_show_or_cross_profiles()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, int profileId) = await PrepareAsync(factory);
        await AskAsync(kid, [1]);
        int id = Assert.Single(await factory.GetRequestsAsync()).Id;
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpResponseMessage revoked = await parent.PostAsJsonAsync("/api/v1/requests/revoke", new { ids = new[] { id } });
        Assert.Equal("succeeded", (await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Empty(factory.Plex.Item("201").Labels);
        Assert.Equal(HttpStatusCode.Created, (await AskAsync(kid, [2])).StatusCode);
        Assert.Equal(RequestState.Pending, (await factory.GetRequestsAsync()).Single(request => request.State != RequestState.Revoked).State);
        Assert.Empty(factory.Plex.Item("201").Labels);
        await CheckAsync(factory);
        Assert.Empty(factory.Plex.Item("201").Labels);

        (HttpClient other, _) = await factory.SignInKidAsync("Bea", tvAgeLimit: 5);
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>(KidRequestApiFactory.RequestsPath)).EnumerateArray());
        HttpResponseMessage attack = await other.PostAsJsonAsync("/api/v1/requests/revoke", new { ids = new[] { id } });
        Assert.Equal(HttpStatusCode.Forbidden, attack.StatusCode);
        Assert.All(await factory.GetRequestsAsync(), request => Assert.Equal(profileId, request.KidProfileId));
    }

    [Fact]
    public async Task A_failed_season_read_preserves_the_previous_observations_and_access()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _) = await PrepareAsync(factory);
        await AskAsync(kid, [1]);
        await CheckAsync(factory);
        factory.Plex.ServerTokenOverride = "unavailable-fixture-token";
        await Assert.ThrowsAnyAsync<Exception>(() => CheckAsync(factory));
        Assert.Equal(RequestState.Monitoring, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
        JsonElement row = Assert.Single((await kid.GetFromJsonAsync<JsonElement>(KidRequestApiFactory.RequestsPath)).EnumerateArray());
        Assert.Equal("partial", row.GetProperty("seasons")[0].GetProperty("availability").GetString());
    }

    [Theory]
    [InlineData("show", "[]")]
    [InlineData("show", "[-1]")]
    [InlineData("show", "[1,1]")]
    [InlineData("show", "[99]")]
    [InlineData("movie", "[1]")]
    public async Task Invalid_seasons_cannot_create_a_request(string kind, string seasons)
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _) = await PrepareAsync(factory);
        HttpResponseMessage response = await kid.PostAsync(KidRequestApiFactory.RequestsPath,
            new StringContent($"{{\"kind\":\"{kind}\",\"tmdbId\":82728,\"seasons\":{seasons}}}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await factory.GetRequestsAsync());
        Assert.Empty(factory.Plex.Item("201").Labels);
    }

    private static async Task<(HttpClient Kid, int Profile)> PrepareAsync(ArrivalApiFactory factory, int tmdbId = 82728)
    {
        SetDetails(factory, "Returning Series", tmdbId: tmdbId);
        if (tmdbId != 82728)
        {
            factory.Plex.Rematch("201", "plex://show/season-fixture", "Fixture show", $"tmdb://{tmdbId}");
            factory.Tmdb.SetBody($"/3/tv/{tmdbId}/content_ratings", """{"results":[{"iso_3166_1":"US","rating":"TV-Y"}]}""");
        }

        await factory.LinkPlexAsync();
        return await factory.SignInKidAsync(tvAgeLimit: 5);
    }

    private static void SetDetails(ArrivalApiFactory factory, string status, bool includeThird = false, int tmdbId = 82728)
    {
        string third = includeThird ? ", {\"season_number\":3,\"episode_count\":8}" : string.Empty;
        factory.Tmdb.SetBody($"/3/tv/{tmdbId}", $$"""{"id":{{tmdbId}},"name":"Fixture show","status":"{{status}}","genres":[],"seasons":[{"season_number":1,"episode_count":10},{"season_number":2,"episode_count":8}{{third}}]}""");
        factory.Services.GetRequiredService<TmdbCache>().Clear();
    }

    private static Task<HttpResponseMessage> AskAsync(HttpClient kid, int[]? seasons, int tmdbId = 82728) =>
        kid.PostAsJsonAsync(KidRequestApiFactory.RequestsPath, new { kind = "show", tmdbId, seasons });

    private static int[] MonitoredSeasons(ArrivalApiFactory factory) =>
        [.. Assert.Single(factory.Sonarr.Library)["seasons"]!.AsArray().Where(season => season!["monitored"]!.GetValue<bool>())
            .Select(season => season!["seasonNumber"]!.GetValue<int>())];

    private static int BackendWrites(ArrivalApiFactory factory) => factory.Sonarr.Requests.Count(request => request.Method != "GET" && request.Path != "/api/v3/tag");

    private static async Task WaitForDispatchJobAsync(ArrivalApiFactory factory)
    {
        JobRunner jobs = factory.Services.GetRequiredService<JobRunner>();
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (!jobs.IsRunning(RequestDispatchJob.JobName))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The season dispatch did not finish.");
    }

    private static async Task DispatchAsync(ArrivalApiFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RequestDispatch>().DispatchPendingAsync(CancellationToken.None);
    }

    private static async Task CheckAsync(ArrivalApiFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RequestArrival>().CheckAsync(CancellationToken.None);
    }
}
