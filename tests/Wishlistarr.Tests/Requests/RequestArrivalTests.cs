using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Library;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Tests.Arr;

namespace Wishlistarr.Tests.Requests;

public sealed class ArrivalApiFactory() : DispatchApiFactory(useLibraryIndex: true)
{
    public ManualTimeProvider Time { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting($"Jobs:Schedules:{LibraryFullRefreshJob.JobName}:Enabled", "false");
        builder.UseSetting($"Jobs:Schedules:{LibraryIncrementalRefreshJob.JobName}:Enabled", "false");
        builder.UseSetting($"Jobs:Schedules:{RequestDispatchJob.JobName}:Enabled", "false");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(this.Time);
        });
    }
}

public sealed class RequestArrivalTests
{
    [Theory]
    [InlineData("radarr", "arr", MediaKind.Movie)]
    [InlineData("sonarr", "arr", MediaKind.Show)]
    [InlineData("seerr", "seerr", MediaKind.Movie)]
    [InlineData("plex", "arr", MediaKind.Show)]
    public async Task A_webhook_labels_the_dispatched_title_only_after_it_is_visible_in_plex(string source, string mode, MediaKind kind)
    {
        using ArrivalApiFactory factory = new();
        (HttpClient parent, HttpClient kid, int id, string key) = await DispatchAsync(factory, mode, kind);

        await SendWebhookAsync(factory, parent, source, key, 1);
        await WaitForJobAsync(factory, JobRunOutcome.Succeeded);
        Assert.Equal(RequestState.Fulfilling, (await factory.GetRequestsAsync()).Single().State);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Empty(factory.Plex.Item(key).Labels);

        Arrive(factory, key, kind);
        await SendWebhookAsync(factory, parent, source, key, 2);
        await factory.WaitForAsync(id, kind == MediaKind.Show ? RequestState.Monitoring : RequestState.Labeled);
        await WaitForJobAsync(factory, JobRunOutcome.Succeeded);

        OwnedLabel label = Assert.Single(await factory.GetLedgerAsync());
        Assert.Contains(label.Label, factory.Plex.Item(key).Labels);
        Assert.Equal(RequestLabeling.LabelSource, label.Source);
        MediaRequestEvent available = Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Available);
        Assert.Equal(RequestState.Fulfilling, available.FromState);
        Assert.Equal(RequestArrival.Component, available.ActorName);
        JsonElement wishlist = await kid.GetFromJsonAsync<JsonElement>("/api/v1/kid/wishlist");
        Assert.Equal("ready", wishlist.GetProperty("items")[0].GetProperty("state").GetString());

        int writes = factory.LabelWrites;
        await SendWebhookAsync(factory, parent, source, key, 3);
        await WaitForJobAsync(factory, JobRunOutcome.Succeeded);
        Assert.Equal(writes, factory.LabelWrites);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == (kind == MediaKind.Show ? RequestState.Monitoring : RequestState.Labeled));
        Assert.Single(await factory.GetLedgerAsync());
    }

    [Theory]
    [InlineData(MediaKind.Movie, "com.plexapp.agents.imdb://tt0133093?lang=en", "tt0133093", "imdb_id")]
    [InlineData(MediaKind.Show, "com.plexapp.agents.thetvdb://2000?lang=en", "2000", "tvdb_id")]
    public async Task Scheduled_polling_recovers_a_missed_webhook_with_only_a_legacy_guid(MediaKind kind, string guid, string externalId, string externalSource)
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", kind);
        int tmdb = TmdbId(kind);
        string results = kind == MediaKind.Movie ? "movie_results" : "tv_results";
        factory.Tmdb.SetBody($"/3/find/{externalId}", $$"""{"{{results}}":[{"id":{{tmdb}}}]}""");
        factory.Plex.Rematch(key, guid, "An unrelated display title");

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await factory.Services.GetRequiredService<JobScheduler>().RunDueAsync();
        await factory.WaitForAsync(id, kind == MediaKind.Show ? RequestState.Monitoring : RequestState.Labeled);
        await WaitForJobAsync(factory, JobRunOutcome.Succeeded);

        OwnedLabel label = Assert.Single(await factory.GetLedgerAsync());
        Assert.Contains(label.Label, factory.Plex.Item(key).Labels);
        Assert.Equal(guid, label.Guid);
        Assert.Contains(factory.Tmdb.Requests, request => request.Path == $"/3/find/{externalId}" && request.Query["external_source"] == externalSource);
        JobRun? run = await factory.Services.GetRequiredService<JobRunner>().GetLastRunAsync(RequestArrivalJob.JobName);
        Assert.Equal(JobRunTrigger.Schedule, run!.Trigger);
        using IServiceScope scope = factory.Services.CreateScope();
        LibraryExternalId resolved = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryExternalIds
            .SingleAsync(row => row.Item!.RatingKey == key && row.Source == LibraryIdSource.Tmdb);
        Assert.Equal(LibraryIdOrigin.TmdbLookup, resolved.Origin);
        Assert.Equal(tmdb.ToString(CultureInfo.InvariantCulture), resolved.Value);
    }

    [Fact]
    public async Task A_plex_outage_keeps_the_request_on_its_way_and_polling_retries_it()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        factory.Plex.ServerTokenOverride = "unavailable-fixture-token";

        await RunAsync(factory, JobRunOutcome.Failed);
        Assert.Equal(RequestState.Fulfilling, (await factory.GetRequestsAsync()).Single().State);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Empty(factory.Plex.Item(key).Labels);

        factory.Plex.ServerTokenOverride = null;
        await RunAsync(factory);
        Assert.Equal(RequestState.Labeled, (await factory.GetRequestsAsync()).Single().State);
        Assert.Single(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task An_interrupted_available_request_finishes_labeling_without_another_available_event()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaRequests>().TransitionAsync(id, RequestState.Available, RequestActor.System(RequestArrival.Component));
        }

        await RunAsync(factory);

        Assert.Equal(RequestState.Labeled, (await factory.GetRequestsAsync()).Single().State);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Available);
        Assert.Single(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Every_matching_movie_copy_is_labeled_but_a_show_with_the_same_id_is_not()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        factory.Plex.Rematch("102", "plex://movie/second-copy", "Another copy", $"tmdb://{TmdbId(MediaKind.Movie)}");
        factory.Plex.Rematch("201", "plex://show/same-number", "A different show", $"tmdb://{TmdbId(MediaKind.Movie)}");

        await RunAsync(factory);

        Assert.Equal(RequestState.Labeled, (await factory.GetRequestsAsync()).Single().State);
        Assert.NotEmpty(factory.Plex.Item(key).Labels);
        Assert.Equal(factory.Plex.Item(key).Labels, factory.Plex.Item("102").Labels);
        Assert.Empty(factory.Plex.Item("201").Labels);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Available);
    }

    [Fact]
    public async Task A_title_rematched_after_indexing_is_not_granted_from_the_stale_index()
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        factory.Plex.Delay = request =>
        {
            if (request.Path == $"/library/metadata/{key}")
            {
                factory.Plex.Rematch(key, "plex://movie/replaced", "An unrelated movie", "tmdb://700");
            }

            return Task.CompletedTask;
        };

        await RunAsync(factory);

        Assert.Equal(RequestState.Fulfilling, (await factory.GetRequestsAsync()).Single().State);
        Assert.Empty(factory.Plex.Item(key).Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Polling_retries_a_failed_legacy_lookup_after_the_cursor_moves_past_the_unchanged_item()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        factory.Plex.Rematch(key, "com.plexapp.agents.imdb://tt0133093?lang=en", "Legacy movie");
        factory.Tmdb.SetBody("/3/find/tt0133093", $$"""{"movie_results":[{"id":{{TmdbId(MediaKind.Movie)}}}]}""");
        factory.Tmdb.Enqueue("/3/find/tt0133093", 503);
        long newer = 1727740800 + 3600;
        factory.Plex.Item("102").UpdatedAtUnix = newer;

        await RunAsync(factory);

        Assert.Equal(RequestState.Fulfilling, (await factory.GetRequestsAsync()).Single().State);
        Assert.Empty(factory.Plex.Item(key).Labels);
        Assert.Equal(1, factory.Tmdb.Count("/3/find/tt0133093"));
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(newer), (await db.LibraryRefreshStates.SingleAsync()).UpdatedCursor);
            Assert.Null((await db.LibraryItems.SingleAsync(item => item.RatingKey == key)).TitleId);
        }

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await factory.Services.GetRequiredService<JobScheduler>().RunDueAsync();
        await factory.WaitForAsync(id, RequestState.Labeled);
        await WaitForJobAsync(factory, JobRunOutcome.Succeeded);

        Assert.Equal(2, factory.Tmdb.Count("/3/find/tt0133093"));
        OwnedLabel label = Assert.Single(await factory.GetLedgerAsync());
        Assert.Contains(label.Label, factory.Plex.Item(key).Labels);
        Assert.Contains(factory.Plex.Requests, request => request.Path == "/library/sections/1/all"
            && request.Query.GetValueOrDefault("updatedAt>>") == (newer - 5).ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(3, false, false)]
    [InlineData(4, false, false)]
    [InlineData(4, true, false)]
    [InlineData(4, true, true)]
    [InlineData(5, false, false)]
    [InlineData(5, true, true)]
    public async Task A_rematch_between_the_arrival_guard_and_the_write_does_not_label_the_replacement(int rematchOnRead, bool keepGuid, bool legacy)
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        if (legacy)
        {
            factory.Plex.Rematch(key, "com.plexapp.agents.imdb://tt0133093?lang=en", "Legacy movie");
            factory.Tmdb.SetBody("/3/find/tt0133093", $$"""{"movie_results":[{"id":{{TmdbId(MediaKind.Movie)}}}]}""");
        }

        int reads = 0;
        factory.Plex.Delay = request =>
        {
            if (request.Path == $"/library/metadata/{key}" && Interlocked.Increment(ref reads) == rematchOnRead)
            {
                string guid = keepGuid ? factory.Plex.Item(key).Guid : "plex://movie/replacement";
                factory.Plex.Rematch(key, guid, "Replacement movie", "imdb://tt0133093", "tmdb://700");
            }

            return Task.CompletedTask;
        };

        await RunAsync(factory);

        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.Equal(0, factory.LabelWrites);
        Assert.Empty(factory.Plex.Item(key).Labels);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Labeled);
    }

    [Fact]
    public async Task A_rematch_after_the_write_fails_verification_and_retains_labels_and_the_original_owner()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient parent, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        string originalGuid = factory.Plex.Item(key).Guid;
        factory.Plex.Item(key).SetLabels(["Shared"]);
        factory.Plex.AfterRequest = request =>
        {
            if (request.Method == "PUT" && request.Path == "/library/sections/1/all")
            {
                factory.Plex.Rematch(key, "plex://movie/replacement", "Replacement movie", "tmdb://700");
            }
        };

        await RunAsync(factory);

        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.Equal(1, factory.LabelWrites);
        OwnedLabel owner = Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal(originalGuid, owner.Guid);
        Assert.Equal(key, owner.RatingKey);
        Assert.Equal("plex://movie/replacement", factory.Plex.Item(key).Guid);
        Assert.Equal(["Shared", owner.Label], factory.Plex.Item(key).Labels);
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Labeled);

        // Once the index reflects the rematch, revoke cannot find the original title and must leave the replacement's labels alone.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().FullAsync(CancellationToken.None);
        }

        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/requests/revoke", new { ids = new[] { id } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement result = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.Equal(1, factory.LabelWrites);
        Assert.Equal(["Shared", owner.Label], factory.Plex.Item(key).Labels);
        Assert.Equal(originalGuid, Assert.Single(await factory.GetLedgerAsync()).Guid);
    }

    [Fact]
    public async Task An_archived_profile_does_not_get_a_label_when_its_download_arrives()
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().KidProfiles
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ArchivedAt, factory.Time.GetUtcNow()));
        }

        Arrive(factory, key, MediaKind.Movie);
        await RunAsync(factory);

        Assert.Equal(RequestState.Fulfilling, (await factory.GetRequestsAsync()).Single().State);
        Assert.Empty(factory.Plex.Item(key).Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    private static int TmdbId(MediaKind kind) => kind == MediaKind.Movie ? DispatchApiFactory.MovieId : FakeArrServer.ShowTmdbId;

    private static async Task<(HttpClient Parent, HttpClient Kid, int Id, string Key)> DispatchAsync(ArrivalApiFactory factory, string mode, MediaKind kind)
    {
        if (kind == MediaKind.Show)
        {
            factory.Tmdb.SetBody($"/3/tv/{TmdbId(kind)}", $$"""{"id":{{TmdbId(kind)}},"name":"Fixture show","status":"Returning Series","seasons":[{"season_number":1,"episode_count":10}]}""");
        }

        string key = kind == MediaKind.Movie ? "101" : "201";
        factory.Plex.DeleteItem("302");
        factory.Plex.Rematch(key, $"local://{key}", "Waiting for the download");
        await factory.LinkPlexAsync();
        (HttpClient kid, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync(mode);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().FullAsync(CancellationToken.None);
        }

        int id = await factory.CreateRequestAsync(profile, kind, TmdbId(kind));
        await factory.ApproveAsync(parent, id);
        await factory.WaitForDispatchAsync(id);
        return (parent, kid, id, key);
    }

    private static void Arrive(ArrivalApiFactory factory, string key, MediaKind kind) =>
        factory.Plex.Rematch(key, $"plex://{(kind == MediaKind.Movie ? "movie" : "show")}/arrived", "Arrived title", $"tmdb://{TmdbId(kind)}");

    private static async Task SendWebhookAsync(ArrivalApiFactory factory, HttpClient parent, string source, string key, int delivery)
    {
        JsonElement endpoints = await parent.GetFromJsonAsync<JsonElement>("/api/v1/settings/webhooks");
        string url = endpoints.EnumerateArray().Single(item => item.GetProperty("source").GetString() == source).GetProperty("url").GetString()!;
        string body = source switch
        {
            "plex" => $$"""{"event":"library.new","Metadata":{"ratingKey":"{{key}}","type":"show"},"delivery":{{delivery}}}""",
            "seerr" => $$"""{"notification_type":"MEDIA_AVAILABLE","media":{"media_type":"movie","tmdbId":"603"},"delivery":{{delivery}}}""",
            "sonarr" => $$"""{"eventType":"Download","series":{"id":1,"tvdbId":2000},"episodes":[{"seasonNumber":1,"episodeNumber":1}],"delivery":{{delivery}}}""",
            _ => $$"""{"eventType":"Download","movie":{"id":1,"tmdbId":603},"delivery":{{delivery}}}""",
        };
        using HttpClient sender = factory.CreateClient();
        using HttpContent content = source == "plex"
            ? new MultipartFormDataContent { { new StringContent(body), "payload" } }
            : new StringContent(body, Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Accepted, (await sender.PostAsync(new Uri(url).PathAndQuery, content)).StatusCode);
        for (int attempt = 0; attempt < 200; attempt++)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            if (!await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().WebhookEvents.AnyAsync(row => row.ProcessedAt == null))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The webhook was not processed.");
    }

    private static async Task RunAsync(ArrivalApiFactory factory, JobRunOutcome outcome = JobRunOutcome.Succeeded)
    {
        JobRunner jobs = factory.Services.GetRequiredService<JobRunner>();
        JobStartResult started = await jobs.StartAsync(RequestArrivalJob.JobName, JobRunTrigger.Schedule);
        Assert.Equal(JobStartStatus.Started, started.Status);
        await started.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(outcome, (await jobs.GetLastRunAsync(RequestArrivalJob.JobName))!.Outcome);
    }

    private static async Task WaitForJobAsync(ArrivalApiFactory factory, JobRunOutcome outcome)
    {
        JobRunner jobs = factory.Services.GetRequiredService<JobRunner>();
        for (int attempt = 0; attempt < 200; attempt++)
        {
            JobRun? run = await jobs.GetLastRunAsync(RequestArrivalJob.JobName);
            if (!jobs.IsRunning(RequestArrivalJob.JobName) && run is not null && run.Outcome != JobRunOutcome.Running)
            {
                Assert.Equal(outcome, run.Outcome);
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The arrival check did not finish.");
    }
}
