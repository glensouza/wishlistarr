using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Library;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Arr;
using Wishlistarr.Tests.Plex;

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
        Assert.Equal("ready", await WishStateAsync(kid));

        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        JsonElement row = Assert.Single((await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests?state=labeled&includeMonitoring=true")).GetProperty("items").EnumerateArray());
        Assert.True(row.GetProperty("canRevoke").GetBoolean());
        Assert.Equal("partial", row.GetProperty("seasons")[0].GetProperty("availability").GetString());

        factory.Plex.Item("2011").LeafCount = 10;
        await CheckAsync(factory);
        Assert.Equal("ready", await WishStateAsync(kid));
    }

    [Fact]
    public async Task Ongoing_shows_keep_monitoring_complete_seasons_and_dispatch_a_new_season_once()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, int id) = await DownloadAsync(factory, [1]);
        factory.Plex.Item("2011").LeafCount = 10;
        await CheckAsync(factory);
        MediaRequest original = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(id, original.Id);
        Assert.Equal(RequestState.Monitoring, original.State);
        Assert.Equal([1], MonitoredSeasons(factory));
        int writes = factory.LabelWrites;

        SetDetails(factory, "Returning Series", includeThird: true, tmdbId: FakeArrServer.ShowTmdbId);
        await CheckAsync(factory);
        int backendWrites = BackendWrites(factory);
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
        Assert.True(BackendWrites(factory) > backendWrites);
        backendWrites = BackendWrites(factory);

        await AskAsync(kid, [3], FakeArrServer.ShowTmdbId);
        await DispatchAsync(factory);
        Assert.Equal(backendWrites, BackendWrites(factory));
        Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(writes, factory.LabelWrites);
        Assert.Single(await factory.GetHistoryAsync(original.Id), item => item.ToState == RequestState.Approved);
    }

    [Fact]
    public async Task A_new_season_waits_for_the_stored_sonarr_series_to_list_every_selection_and_is_retried_after_a_pause()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, _) = await DownloadAsync(factory, [1]);
        factory.Plex.Item("2011").LeafCount = 10;
        await CheckAsync(factory);
        JsonObject stored = Assert.Single(factory.Sonarr.Library);
        stored["seasons"]!.AsArray().RemoveAt(3);
        SetDetails(factory, "Returning Series", includeThird: true, tmdbId: FakeArrServer.ShowTmdbId);
        int labelWrites = factory.LabelWrites;
        int backendWrites = BackendWrites(factory);

        await AskAsync(kid, [3], FakeArrServer.ShowTmdbId);
        await WaitForDispatchJobAsync(factory);
        MediaRequest waiting = Assert.Single(await factory.GetRequestsAsync());
        Assert.Null(waiting.DispatchedAt);
        Assert.Equal(RequestState.Monitoring, waiting.State);
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
        Assert.Equal(backendWrites, BackendWrites(factory));
        Assert.DoesNotContain(await factory.GetHistoryAsync(waiting.Id), item => item.ToState == RequestState.Failed);
        int lookups = factory.Sonarr.Sent("GET", "/api/v3/series/lookup").Count();
        Assert.True(lookups > 0);

        stored["seasons"]!.AsArray().Add(new JsonObject { ["seasonNumber"] = 3, ["monitored"] = false });
        await DispatchAsync(factory);
        Assert.Equal(lookups, factory.Sonarr.Sent("GET", "/api/v3/series/lookup").Count());
        Assert.Null(Assert.Single(await factory.GetRequestsAsync()).DispatchedAt);

        factory.Time.Advance(RequestDispatchBackoff.Delay);
        await DispatchAsync(factory);
        await WaitForDispatchJobAsync(factory);
        MediaRequest delivered = Assert.Single(await factory.GetRequestsAsync());
        Assert.NotNull(delivered.DispatchedAt);
        Assert.Equal(RequestState.Monitoring, delivered.State);
        Assert.Equal([1, 3], MonitoredSeasons(factory));
        Assert.Equal(labelWrites, factory.LabelWrites);
        int writes = BackendWrites(factory);
        await DispatchAsync(factory);
        Assert.Equal(writes, BackendWrites(factory));
    }

    [Fact]
    public async Task A_refused_season_dispatch_leaves_a_monitored_show_monitoring_and_unsent()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, int id) = await DownloadAsync(factory, [1]);
        factory.Plex.Item("2011").LeafCount = 10;
        await CheckAsync(factory);
        await factory.Sonarr.DisposeAsync();

        await AskAsync(kid, [2], FakeArrServer.ShowTmdbId);
        await WaitForDispatchJobAsync(factory);

        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Monitoring, request.State);
        Assert.Null(request.DispatchedAt);
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Failed);
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
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
    public async Task An_instant_labeled_show_can_start_a_download_when_a_kid_asks_for_another_season()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _) = await PrepareAsync(factory, FakeArrServer.ShowTmdbId);
        await factory.ConfiguredParentAsync("arr");
        factory.Plex.Item("2011").LeafCount = 10;

        Assert.Equal(HttpStatusCode.Created, (await AskAsync(kid, [1], FakeArrServer.ShowTmdbId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(kid, [2], FakeArrServer.ShowTmdbId)).StatusCode);
        await WaitForDispatchJobAsync(factory);

        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.NotNull(request.DispatchedAt);
        Assert.Equal(RequestState.Monitoring, request.State);
        Assert.Equal([1, 2], MonitoredSeasons(factory));
    }

    [Fact]
    public async Task A_season_request_at_available_after_a_label_failure_does_not_stop_the_next_arrival_run_labeling_it()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, int id) = await DownloadAsync(factory, [1]);
        factory.Services.GetRequiredService<TransportFailureMediaServer>().FailAfterNextWrite = new HttpRequestException("The connection closed.");
        await CheckAsync(factory);
        MediaRequest stuck = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Available, stuck.State);
        Assert.NotNull(stuck.DispatchedAt);

        Assert.Equal(HttpStatusCode.OK, (await AskAsync(kid, [2], FakeArrServer.ShowTmdbId)).StatusCode);
        await WaitForDispatchJobAsync(factory);
        MediaRequest merged = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Available, merged.State);
        Assert.NotNull(merged.DispatchedAt);

        await CheckAsync(factory);
        Assert.Equal(RequestState.Monitoring, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Single(await factory.GetLedgerAsync());
        Assert.Contains(await factory.GetHistoryAsync(id), item => item.FromState == RequestState.Available && item.ToState == RequestState.Monitoring);
    }

    [Fact]
    public async Task Seasons_an_all_season_request_already_covers_are_not_new_selections()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, int id) = await DownloadAsync(factory, null);
        await CheckAsync(factory);
        MediaRequest before = Assert.Single(await factory.GetRequestsAsync());
        Assert.True(before.AllSeasons);
        Assert.Equal(RequestState.Monitoring, before.State);
        int backendWrites = BackendWrites(factory);

        Assert.Equal(HttpStatusCode.OK, (await AskAsync(kid, [2], FakeArrServer.ShowTmdbId)).StatusCode);
        await WaitForDispatchJobAsync(factory);

        MediaRequest after = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(id, after.Id);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.DispatchedAt, after.DispatchedAt);
        Assert.Equal(backendWrites, BackendWrites(factory));
    }

    [Fact]
    public async Task A_merge_that_loses_a_race_for_the_request_answers_conflict()
    {
        using ArrivalApiFactory factory = new();
        (_, int profileId, int id) = await DownloadAsync(factory, [1]);
        factory.Plex.Item("2011").LeafCount = 10;
        await CheckAsync(factory);
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        MediaRequests requests = scope.ServiceProvider.GetRequiredService<MediaRequests>();
        string connection = db.Database.GetConnectionString()!;
        db.SavingChanges += (_, _) =>
        {
            using WishlistarrDbContext other = new(new DbContextOptionsBuilder<WishlistarrDbContext>().UseSqlite(connection).Options);
            other.Database.ExecuteSqlRaw("UPDATE MediaRequests SET Version = Version + 1 WHERE Id = {0}", id);
        };

        RequestResult result = await requests.CreateAsync(
            profileId,
            new TitleKey(MediaKind.Show, FakeArrServer.ShowTmdbId),
            "Fixture show",
            RequestActor.Kid(profileId, "Ada"),
            seasons: [2]);

        Assert.Equal(RequestOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task An_ended_show_with_every_requested_season_present_goes_straight_to_labeled_when_first_labeled()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id) = await DownloadAsync(factory, [1], "Ended");
        factory.Plex.Item("2011").LeafCount = 10;

        await CheckAsync(factory);

        Assert.Equal(RequestState.Labeled, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
        MediaRequestEvent[] events = [.. (await factory.GetHistoryAsync(id)).Where(item => item.ToState is RequestState.Monitoring or RequestState.Labeled)];
        Assert.Equal([(RequestState.Available, RequestState.Monitoring), (RequestState.Monitoring, RequestState.Labeled)], events.Select(item => (item.FromState!.Value, item.ToState)));
    }

    [Fact]
    public async Task A_new_season_of_a_finished_show_moves_it_back_to_monitoring_and_the_kid_history_hides_the_moves()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, int id) = await DownloadAsync(factory, [1], "Ended");
        factory.Plex.Item("2011").LeafCount = 10;
        await CheckAsync(factory);
        Assert.Equal(RequestState.Labeled, Assert.Single(await factory.GetRequestsAsync()).State);

        Assert.Equal(HttpStatusCode.OK, (await AskAsync(kid, [2], FakeArrServer.ShowTmdbId)).StatusCode);
        await WaitForDispatchJobAsync(factory);

        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Monitoring, request.State);
        Assert.NotNull(request.DispatchedAt);
        Assert.Equal([1, 2], MonitoredSeasons(factory));
        MediaRequestEvent back = Assert.Single(await factory.GetHistoryAsync(id), item => item.FromState == RequestState.Labeled && item.ToState == RequestState.Monitoring);
        Assert.Equal("season-request", back.ActorName);

        JsonElement[] history = await kid.GetFromJsonAsync<JsonElement[]>("/api/v1/kid/requests/history") ?? [];
        Assert.Equal(["ready", "approved", "asked"], history.Select(entry => entry.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task A_failing_tmdb_does_not_stop_arrival_from_labeling_a_show()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, int id) = await DownloadAsync(factory, [1]);
        factory.Services.GetRequiredService<TmdbCache>().Clear();
        factory.Tmdb.Enqueue($"/3/tv/{FakeArrServer.ShowTmdbId}", 404);

        await CheckAsync(factory);

        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Monitoring, request.State);
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Monitoring);

        Assert.Equal(SeasonAvailability.Unknown, Assert.Single(await SeasonsAsync(factory, id)).Availability);
        Assert.Equal("onTheWay", await WishStateAsync(kid));

        await CheckAsync(factory);
        MediaRequest recovered = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(SeasonAvailability.Partial, Assert.Single(await SeasonsAsync(factory, recovered.Id)).Availability);
        Assert.Equal("ready", await WishStateAsync(kid));
    }

    [Fact]
    public async Task A_tmdb_that_is_not_configured_does_not_stop_arrival_or_the_missing_title_rule()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id) = await DownloadAsync(factory, [1]);
        factory.Services.GetRequiredService<TransportFailureMediaServer>().FailAfterNextWrite = new HttpRequestException("The connection closed.");
        await CheckAsync(factory);
        Assert.Equal(RequestState.Available, Assert.Single(await factory.GetRequestsAsync()).State);
        factory.TmdbKeys.Key = null;

        factory.Plex.DeleteItem("201");
        factory.Time.Advance(TimeSpan.FromHours(25));
        await CheckAsync(factory);

        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Failed, request.State);
        Assert.Equal(RequestLabeling.NoLongerInLibraryReason, (await factory.GetHistoryAsync(id)).Last().Reason);
    }

    [Fact]
    public async Task A_tmdb_that_is_not_configured_still_lets_an_arrived_show_be_labeled()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id) = await DownloadAsync(factory, [1]);
        factory.TmdbKeys.Key = null;

        await CheckAsync(factory);

        Assert.Equal(RequestState.Monitoring, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Monitoring);
    }

    [Fact]
    public async Task A_plex_only_season_and_a_season_without_a_tmdb_count_do_not_hold_back_an_all_season_request()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, int id) = await DownloadAsync(factory, [1], "Ended");
        factory.Plex.Item("2011").LeafCount = 10;
        AddSeason(factory, "2013", 3, 4);
        await CheckAsync(factory);
        Assert.Equal(RequestState.Labeled, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Contains(await SeasonsAsync(factory, id), season => season.Number == 3 && !season.Requested);

        Assert.Equal(HttpStatusCode.OK, (await AskAsync(kid, null, FakeArrServer.ShowTmdbId)).StatusCode);
        await WaitForDispatchJobAsync(factory);
        await CheckAsync(factory);
        Assert.Equal(RequestState.Monitoring, Assert.Single(await factory.GetRequestsAsync()).State);
        AddSeason(factory, "2012", 2, 8);
        await CheckAsync(factory);

        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.True(request.AllSeasons);
        Assert.Equal(RequestState.Labeled, request.State);
        Assert.Equal(id, request.Id);
    }

    [Fact]
    public async Task The_approval_queue_lists_the_requested_seasons_or_marks_the_whole_show()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetBody("/3/tv/82728", """{"id":82728,"name":"Bluey","status":"Returning Series","vote_average":8.8,"seasons":[{"season_number":0,"episode_count":2},{"season_number":1,"episode_count":10},{"season_number":2,"episode_count":8}]}""");
        (HttpClient ada, _) = await factory.SignInKidAsync("Ada", tvAgeLimit: 5);
        (HttpClient bea, _) = await factory.SignInKidAsync("Bea", tvAgeLimit: 5);
        Assert.Equal(HttpStatusCode.Created, (await AskAsync(ada, [0, 2])).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AskAsync(bea, null)).StatusCode);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        JsonElement[] items = [.. (await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests/queue")).GetProperty("items").EnumerateArray()];

        JsonElement explicitSeasons = Assert.Single(items, item => item.GetProperty("kidName").GetString() == "Ada");
        Assert.Equal([0, 2], explicitSeasons.GetProperty("seasons").EnumerateArray().Select(season => season.GetProperty("number").GetInt32()));
        Assert.False(explicitSeasons.GetProperty("allSeasons").GetBoolean());
        JsonElement whole = Assert.Single(items, item => item.GetProperty("kidName").GetString() == "Bea");
        Assert.Empty(whole.GetProperty("seasons").EnumerateArray());
        Assert.True(whole.GetProperty("allSeasons").GetBoolean());
    }

    [Fact]
    public async Task A_monitored_show_does_not_refresh_the_plex_index_on_every_arrival_run()
    {
        using ArrivalApiFactory factory = new();
        await DownloadAsync(factory, [1]);
        await CheckAsync(factory);
        Assert.Equal(RequestState.Monitoring, Assert.Single(await factory.GetRequestsAsync()).State);
        int scans = factory.LibraryScans;

        await CheckAsync(factory);
        await CheckAsync(factory);

        Assert.Equal(scans, factory.LibraryScans);
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
        await CheckAsync(factory);
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

    [Fact]
    public async Task A_refused_extra_season_keeps_a_downloading_show_fulfilling_and_it_is_labeled_when_the_first_season_lands()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, int id) = await DownloadAsync(factory, [1], arrive: false);
        await factory.Sonarr.DisposeAsync();

        Assert.Equal(HttpStatusCode.OK, (await AskAsync(kid, [2], FakeArrServer.ShowTmdbId)).StatusCode);
        await WaitForDispatchJobAsync(factory);
        MediaRequest waiting = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Fulfilling, waiting.State);
        Assert.Null(waiting.DispatchedAt);
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Failed);

        await DispatchAsync(factory);
        Assert.Equal(RequestState.Fulfilling, Assert.Single(await factory.GetRequestsAsync()).State);

        Arrive(factory);
        await CheckAsync(factory);
        Assert.Equal(RequestState.Monitoring, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
    }

    [Fact]
    public async Task Backoff_entries_are_dropped_once_a_request_is_revoked()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id) = await DownloadAsync(factory, [1]);
        await CheckAsync(factory);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        RequestDispatchBackoff backoff = factory.Services.GetRequiredService<RequestDispatchBackoff>();
        backoff.Defer(id, factory.Time.GetUtcNow());
        await DispatchAsync(factory);
        Assert.Contains(id, backoff.Waiting);

        await parent.PostAsJsonAsync("/api/v1/requests/revoke", new { ids = new[] { id } });
        await DispatchAsync(factory);

        Assert.Empty(backoff.Waiting);
    }

    [Fact]
    public async Task A_tmdb_season_without_episodes_does_not_hold_back_an_all_season_request()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id) = await DownloadAsync(factory, null, "Ended");
        factory.Tmdb.SetBody($"/3/tv/{FakeArrServer.ShowTmdbId}", $$"""{"id":{{FakeArrServer.ShowTmdbId}},"name":"Fixture show","status":"Ended","genres":[],"seasons":[{"season_number":1,"episode_count":10},{"season_number":2,"episode_count":0}]}""");
        factory.Services.GetRequiredService<TmdbCache>().Clear();
        factory.Plex.Item("2011").LeafCount = 10;

        await CheckAsync(factory);

        Assert.Equal(RequestState.Labeled, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(id, Assert.Single(await factory.GetRequestsAsync()).Id);
    }

    [Fact]
    public async Task A_malformed_tmdb_answer_does_not_stop_arrival_from_labeling_a_show()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id) = await DownloadAsync(factory, [1]);
        factory.Tmdb.SetBody($"/3/tv/{FakeArrServer.ShowTmdbId}", "{not json");
        factory.Services.GetRequiredService<TmdbCache>().Clear();

        await CheckAsync(factory);

        Assert.Equal(RequestState.Monitoring, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Monitoring);
    }

    [Fact]
    public async Task A_plex_failure_reading_seasons_does_not_stop_a_new_arrival_being_labeled()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id) = await DownloadAsync(factory, [1]);
        factory.Plex.Delay = request => request.Path.EndsWith("/children", StringComparison.Ordinal)
            ? throw new InvalidOperationException("The server is down.")
            : Task.CompletedTask;

        await CheckAsync(factory);

        Assert.Equal(RequestState.Monitoring, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Monitoring);
    }

    [Fact]
    public async Task A_monitored_show_that_is_not_visible_keeps_its_last_season_observation()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id) = await DownloadAsync(factory, [1]);
        await CheckAsync(factory);
        Assert.Equal(SeasonAvailability.Partial, Assert.Single(await SeasonsAsync(factory, id)).Availability);

        factory.Plex.DeleteItem("201");
        await CheckAsync(factory);

        MediaRequestSeason season = Assert.Single(await SeasonsAsync(factory, id));
        Assert.Equal(SeasonAvailability.Partial, season.Availability);
        Assert.Equal(1, season.AvailableEpisodes);
    }

    [Fact]
    public async Task A_refused_extra_season_keeps_a_fulfilling_request_that_has_no_service_id()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, int id) = await DownloadAsync(factory, [1], arrive: false);
        await UpdateRequestAsync(factory, id, setters => setters.SetProperty(row => row.ExternalId, (int?)null));
        await factory.Sonarr.DisposeAsync();

        Assert.Equal(HttpStatusCode.OK, (await AskAsync(kid, [2], FakeArrServer.ShowTmdbId)).StatusCode);
        await WaitForDispatchJobAsync(factory);
        MediaRequest waiting = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Fulfilling, waiting.State);
        Assert.Null(waiting.DispatchedAt);
        Assert.Null(waiting.ExternalId);
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Failed);

        Arrive(factory);
        await CheckAsync(factory);
        Assert.Equal(RequestState.Monitoring, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
    }

    [Fact]
    public async Task A_refused_resend_keeps_an_approved_request_that_has_a_service_id_and_is_not_retried_within_the_pause()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id) = await DownloadAsync(factory, [1], arrive: false);
        await UpdateRequestAsync(factory, id, setters => setters
            .SetProperty(row => row.State, RequestState.Approved)
            .SetProperty(row => row.ExternalId, 5)
            .SetProperty(row => row.DispatchedAt, (DateTimeOffset?)null));
        Assert.Single(factory.Sonarr.Library)["seasons"]!.AsArray().RemoveAt(1);
        int lookups = factory.Sonarr.Sent("GET", "/api/v3/series/lookup").Count();

        await DispatchAsync(factory);
        Assert.Equal(lookups + 1, factory.Sonarr.Sent("GET", "/api/v3/series/lookup").Count());
        await DispatchAsync(factory);

        Assert.Equal(lookups + 1, factory.Sonarr.Sent("GET", "/api/v3/series/lookup").Count());
        Assert.Equal(RequestState.Approved, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Failed);

        factory.Time.Advance(RequestDispatchBackoff.Delay);
        await DispatchAsync(factory);
        Assert.Equal(lookups + 2, factory.Sonarr.Sent("GET", "/api/v3/series/lookup").Count());
    }

    [Fact]
    public async Task A_fulfilling_request_whose_own_send_was_never_accepted_still_fails_when_refused()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id) = await DownloadAsync(factory, [1], arrive: false);
        await UpdateRequestAsync(factory, id, setters => setters
            .SetProperty(row => row.ExternalId, (int?)null)
            .SetProperty(row => row.AcceptedAt, (DateTimeOffset?)null)
            .SetProperty(row => row.DispatchedAt, (DateTimeOffset?)null));
        await factory.Sonarr.DisposeAsync();

        await DispatchAsync(factory);

        Assert.Equal(RequestState.Failed, Assert.Single(await factory.GetRequestsAsync()).State);
    }

    [Fact]
    public async Task A_retried_request_whose_send_succeeded_is_kept_when_a_later_extra_season_is_refused()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id) = await DownloadAsync(factory, [1], arrive: false);
        await UpdateRequestAsync(factory, id, setters => setters
            .SetProperty(row => row.ExternalId, (int?)null)
            .SetProperty(row => row.AcceptedAt, (DateTimeOffset?)null)
            .SetProperty(row => row.DispatchedAt, (DateTimeOffset?)null));
        await DispatchAsync(factory);
        MediaRequest sent = Assert.Single(await factory.GetRequestsAsync());
        Assert.NotNull(sent.AcceptedAt);

        await UpdateRequestAsync(factory, id, setters => setters
            .SetProperty(row => row.ExternalId, (int?)null)
            .SetProperty(row => row.DispatchedAt, (DateTimeOffset?)null));
        await factory.Sonarr.DisposeAsync();
        await DispatchAsync(factory);

        Assert.Equal(RequestState.Fulfilling, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Failed);
    }

    [Fact]
    public async Task Specials_alone_do_not_make_an_all_season_show_ready()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, int id) = await DownloadAsync(factory, null);
        factory.Services.GetRequiredService<TmdbCache>().Clear();
        factory.Tmdb.Enqueue($"/3/tv/{FakeArrServer.ShowTmdbId}", 404);
        await CheckAsync(factory);
        await AddObservedSeasonAsync(factory, id, 0, 2);
        Assert.Equal("onTheWay", await WishStateAsync(kid));

        await AddObservedSeasonAsync(factory, id, 1, 3);
        Assert.Equal("ready", await WishStateAsync(kid));
    }

    private static async Task AddObservedSeasonAsync(ArrivalApiFactory factory, int id, int number, int episodes)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        db.Set<MediaRequestSeason>().Add(new MediaRequestSeason { MediaRequestId = id, Number = number, AvailableEpisodes = episodes });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_monitored_show_counts_as_ready_when_a_requested_season_has_episodes_whatever_its_availability()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient kid, _, int id) = await DownloadAsync(factory, [1]);
        factory.Services.GetRequiredService<TmdbCache>().Clear();
        factory.Tmdb.Enqueue($"/3/tv/{FakeArrServer.ShowTmdbId}", 404);
        await CheckAsync(factory);
        Assert.Equal("onTheWay", await WishStateAsync(kid));

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Set<MediaRequestSeason>()
                .Where(season => season.MediaRequestId == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(season => season.AvailableEpisodes, 2));
        }

        MediaRequestSeason season = Assert.Single(await SeasonsAsync(factory, id));
        Assert.Equal(SeasonAvailability.Unknown, season.Availability);
        Assert.Equal("ready", await WishStateAsync(kid));
    }

    private static async Task UpdateRequestAsync(
        ArrivalApiFactory factory,
        int id,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<MediaRequest>> set)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().MediaRequests
            .Where(row => row.Id == id)
            .ExecuteUpdateAsync(set);
    }

    private static async Task<(HttpClient Kid, int Profile, int Id)> DownloadAsync(ArrivalApiFactory factory, int[]? seasons, string status = "Returning Series", bool arrive = true)
    {
        SetDetails(factory, status, tmdbId: FakeArrServer.ShowTmdbId);
        factory.Tmdb.SetBody($"/3/tv/{FakeArrServer.ShowTmdbId}/content_ratings", """{"results":[{"iso_3166_1":"US","rating":"TV-Y"}]}""");
        factory.Plex.Rematch("201", "local://201", "Waiting for the download");
        await factory.LinkPlexAsync();
        (HttpClient kid, int profile) = await factory.SignInKidAsync(tvAgeLimit: 5);
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().FullAsync(CancellationToken.None);
        }

        int id = await CreateRequestAsync(factory, profile, seasons);
        await factory.ApproveAsync(parent, id);
        await factory.WaitForDispatchAsync(id);
        if (arrive)
        {
            Arrive(factory);
        }

        return (kid, profile, id);
    }

    private static async Task<int> CreateRequestAsync(ArrivalApiFactory factory, int profile, int[]? seasons)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        RequestResult made = await scope.ServiceProvider.GetRequiredService<MediaRequests>().CreateAsync(
            profile, new TitleKey(MediaKind.Show, FakeArrServer.ShowTmdbId), "Fixture show", RequestActor.Kid(profile, "Ada"), seasons: seasons);
        return made.Request!.Id;
    }

    private static async Task<List<MediaRequestSeason>> SeasonsAsync(ArrivalApiFactory factory, int id)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Set<MediaRequestSeason>()
            .AsNoTracking().Where(season => season.MediaRequestId == id).OrderBy(season => season.Number).ToListAsync();
    }

    private static async Task<string?> WishStateAsync(HttpClient kid) =>
        (await kid.GetFromJsonAsync<JsonElement>("/api/v1/kid/wishlist")).GetProperty("items")[0].GetProperty("state").GetString();

    private static void Arrive(ArrivalApiFactory factory) =>
        factory.Plex.Rematch("201", "plex://show/arrived", "Arrived title", $"tmdb://{FakeArrServer.ShowTmdbId}");

    private static void AddSeason(ArrivalApiFactory factory, string key, int number, int episodes) =>
        factory.Plex.AddItem(new FakePlexServer.FakeItem(key, "season", $"plex://season/{key}", $"Season {number}", 2)
        {
            ParentRatingKey = "201",
            Index = number,
            LeafCount = episodes,
        });

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
