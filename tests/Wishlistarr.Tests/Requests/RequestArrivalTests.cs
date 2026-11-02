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
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Labels;
using Wishlistarr.Api.Library;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Plex;
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
            services.AddSingleton(provider => new TransportFailureMediaServer(provider.GetRequiredService<PlexMediaServer>()));
            services.RemoveAll<IMediaServer>();
            services.RemoveAll<ILibraryScanner>();
            services.AddSingleton<ILibraryScanner>(provider => provider.GetRequiredService<TransportFailureMediaServer>());
            services.AddSingleton<IMediaServer>(provider => provider.GetRequiredService<TransportFailureMediaServer>());
        });
    }
}

public sealed class RequestArrivalTests
{
    private static readonly TimeSpan LegacyRetryAfter = TimeSpan.FromMinutes(30);

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
        await factory.WaitForAsync(id, RequestState.Labeled);
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
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Labeled);
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

        factory.Time.Advance(LegacyRetryAfter);
        await factory.Services.GetRequiredService<JobScheduler>().RunDueAsync();
        await factory.WaitForAsync(id, RequestState.Labeled);
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
        await WaitForJobAsync(factory, JobRunOutcome.Succeeded);
        Assert.Equal(1, factory.Tmdb.Count("/3/find/tt0133093"));
        Assert.Equal(RequestState.Fulfilling, (await factory.GetRequestsAsync()).Single().State);

        factory.Time.Advance(LegacyRetryAfter);
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
    [InlineData(2, true, false)]
    [InlineData(3, false, false)]
    [InlineData(3, true, false)]
    [InlineData(4, false, false)]
    [InlineData(4, true, false)]
    [InlineData(4, true, true)]
    [InlineData(5, false, false)]
    [InlineData(5, true, false)]
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

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task A_rematch_that_keeps_the_guid_and_is_undone_by_the_next_read_still_blocks_the_write(int rematchOnRead)
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        string guid = factory.Plex.Item(key).Guid;
        int reads = 0;
        factory.Plex.Delay = request =>
        {
            if (request.Path == $"/library/metadata/{key}")
            {
                int read = Interlocked.Increment(ref reads);
                if (read == rematchOnRead)
                {
                    factory.Plex.Rematch(key, guid, "Replacement movie", "tmdb://700");
                }
                else if (read == rematchOnRead + 1)
                {
                    factory.Plex.Rematch(key, guid, "Arrived title", $"tmdb://{TmdbId(MediaKind.Movie)}");
                }
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
    public async Task A_rematch_after_the_write_that_keeps_the_guid_does_not_end_labeled()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        string guid = factory.Plex.Item(key).Guid;
        factory.Plex.AfterRequest = request =>
        {
            if (request.Method == "PUT")
            {
                factory.Plex.Rematch(key, guid, "Replacement movie", "tmdb://700");
            }
        };

        await RunAsync(factory);

        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.Equal(1, factory.LabelWrites);
        OwnedLabel owner = Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal(guid, owner.Guid);
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
    public async Task A_plex_failure_while_the_label_is_written_leaves_the_request_available_and_polling_retries_it()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        factory.Plex.Delay = request => request.Method == "PUT"
            ? throw new InvalidOperationException("The server is down.")
            : Task.CompletedTask;

        await RunAsync(factory);

        Assert.Equal(RequestState.Available, (await factory.GetRequestsAsync()).Single().State);
        Assert.Empty(factory.Plex.Item(key).Labels);
        OwnedLabel kept = Assert.Single(await factory.GetLedgerAsync());
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Failed);

        factory.Plex.Delay = null;
        await RunAsync(factory);

        Assert.Equal(RequestState.Labeled, (await factory.GetRequestsAsync()).Single().State);
        Assert.Equal(kept.Id, Assert.Single(await factory.GetLedgerAsync()).Id);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Available);
    }

    [Fact]
    public async Task Missing_expected_identities_are_skipped_instead_of_throwing_a_lookup_error()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        using IServiceScope scope = factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        await services.GetRequiredService<MediaRequests>().TransitionAsync(id, RequestState.Available, RequestActor.System(RequestArrival.Component));
        KidProfile profile = await services.GetRequiredService<WishlistarrDbContext>().KidProfiles.AsNoTracking().SingleAsync();
        MediaItem match = new(key, factory.Plex.Item(key).Guid, "Arrived title", null, MediaKind.Movie, "1", [new ExternalId(ExternalIdSource.Tmdb, TmdbId(MediaKind.Movie).ToString(CultureInfo.InvariantCulture))]);

        RequestLabelResult result = await services.GetRequiredService<RequestLabeling>().LabelLockedAsync(
            id, [match], profile, RequestActor.System(RequestArrival.Component), expectedItems: new Dictionary<string, MediaItemLabels>());

        Assert.Equal(RequestState.Failed, result.Result.Request!.State);
        Assert.Equal(0, factory.LabelWrites);
        Assert.Equal("The title is no longer in the library.", (await factory.GetHistoryAsync(id)).Single(item => item.ToState == RequestState.Failed).Reason);
    }

    [Fact]
    public async Task The_profile_label_gate_is_held_from_the_profile_check_until_the_label_is_written()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        int profileId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            profileId = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().KidProfiles.Select(row => row.Id).SingleAsync();
        }

        using IServiceScope archiveScope = factory.Services.CreateScope();
        Task<bool>? archive = null;
        bool archivedDuringWrite = true;
        int reads = 0;
        factory.Plex.Delay = async request =>
        {
            // The second read is the label writer's own, after the profile was checked.
            if (request.Path == $"/library/metadata/{key}" && Interlocked.Increment(ref reads) == 2)
            {
                archive = archiveScope.ServiceProvider.GetRequiredService<KidProfiles>().ArchiveAsync(profileId, false);
                await Task.Delay(500);
                archivedDuringWrite = archive.IsCompleted;
            }
        };

        await RunAsync(factory);

        Assert.False(archivedDuringWrite);
        Assert.True(await archive!);
        Assert.Equal(RequestState.Labeled, (await factory.GetRequestsAsync()).Single().State);
        Assert.Single(await factory.GetLedgerAsync());
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Labeled);
    }

    [Fact]
    public async Task Polling_skips_legacy_retries_without_a_tmdb_key_and_without_waiting_for_the_refresh_gate()
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        await IndexUnresolvedAsync(factory, (key, "com.plexapp.agents.imdb://tt0133093?lang=en", "tt0133093", "movie_results", MediaKind.Movie));
        await AgeLibraryAsync(factory, LegacyRetryAfter);
        factory.TmdbKeys.Key = null;
        int reads = factory.Plex.Count("GET", $"/library/metadata/{key}");

        await RetryWhileGateHeldAsync(factory);

        Assert.Equal(reads, factory.Plex.Count("GET", $"/library/metadata/{key}"));
        Assert.Equal(1, factory.Tmdb.Count("/3/find/tt0133093"));
    }

    [Fact]
    public async Task Polling_does_not_take_the_refresh_gate_or_ask_plex_while_no_legacy_item_is_due()
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        await IndexUnresolvedAsync(factory, (key, "com.plexapp.agents.imdb://tt0133093?lang=en", "tt0133093", "movie_results", MediaKind.Movie));
        await AgeLibraryAsync(factory, LegacyRetryAfter - TimeSpan.FromSeconds(30));
        int reads = factory.Plex.Count("GET", $"/library/metadata/{key}");

        await RetryWhileGateHeldAsync(factory);

        Assert.Equal(reads, factory.Plex.Count("GET", $"/library/metadata/{key}"));
        Assert.Equal(1, factory.Tmdb.Count("/3/find/tt0133093"));

        await AgeLibraryAsync(factory, LegacyRetryAfter);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().RetryUnresolvedAsync(CancellationToken.None);
        }

        Assert.Equal(2, factory.Tmdb.Count("/3/find/tt0133093"));
        Assert.NotNull(await TitleIdAsync(factory, key));
    }

    [Fact]
    public async Task Polling_drops_a_legacy_item_that_plex_no_longer_has_instead_of_retrying_it()
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        await IndexUnresolvedAsync(factory, (key, "com.plexapp.agents.imdb://tt0133093?lang=en", "tt0133093", "movie_results", MediaKind.Movie));
        factory.Plex.DeleteItem(key);
        await AgeLibraryAsync(factory, LegacyRetryAfter);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().RetryUnresolvedAsync(CancellationToken.None);
            Assert.False(await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryItems.AnyAsync(item => item.RatingKey == key));
        }

        int reads = factory.Plex.Count("GET", $"/library/metadata/{key}");
        await AgeLibraryAsync(factory, LegacyRetryAfter);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().RetryUnresolvedAsync(CancellationToken.None);
        }

        Assert.Equal(reads, factory.Plex.Count("GET", $"/library/metadata/{key}"));
    }

    [Fact]
    public async Task One_legacy_item_that_fails_to_save_does_not_stop_the_arrival_check()
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Show);
        await IndexUnresolvedAsync(
            factory,
            ("101", "com.plexapp.agents.imdb://tt0133093?lang=en", "tt0133093", "movie_results", MediaKind.Movie),
            (key, "com.plexapp.agents.thetvdb://2000?lang=en", "2000", "tv_results", MediaKind.Show));
        await AgeLibraryAsync(factory, LegacyRetryAfter);
        factory.Plex.Item("101").UpdatedAtUnix = 1;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER block_legacy_item BEFORE UPDATE ON LibraryItems WHEN OLD.RatingKey = '101' BEGIN SELECT RAISE(ABORT, 'blocked'); END;");
        }

        await RunAsync(factory);

        Assert.Equal(RequestState.Labeled, (await factory.GetRequestsAsync()).Single().State);
        Assert.Contains(factory.Plex.Requests, request => request.Path == "/library/metadata/101");
        Assert.Single(await factory.GetLedgerAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Legacy_retries_stop_after_one_attempt_when_plex_cannot_be_reached(int kind)
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Show);
        await IndexUnresolvedAsync(
            factory,
            ("101", "com.plexapp.agents.imdb://tt0133093?lang=en", "tt0133093", "movie_results", MediaKind.Movie),
            (key, "com.plexapp.agents.thetvdb://2000?lang=en", "2000", "tv_results", MediaKind.Show));
        await AgeLibraryAsync(factory, LegacyRetryAfter);
        TransportFailureMediaServer plex = factory.Services.GetRequiredService<TransportFailureMediaServer>();
        plex.GetEntryFailure = kind switch
        {
            0 => new HttpRequestException("The connection was refused."),
            1 => new TaskCanceledException("The request timed out."),
            2 => new MediaServerException("Plex could not be reached."),
            3 => new MediaServerException("Plex rejected the saved sign-in.", System.Net.HttpStatusCode.Unauthorized),
            _ => new MediaServerException("Plex answered 503.", System.Net.HttpStatusCode.ServiceUnavailable),
        };

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().RetryUnresolvedAsync(CancellationToken.None);
        }

        Assert.Equal(1, plex.GetEntryCalls);
    }

    [Fact]
    public async Task A_legacy_item_with_a_plex_error_status_is_skipped_and_stamped_so_later_items_still_resolve()
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Show);
        await IndexUnresolvedAsync(
            factory,
            ("101", "com.plexapp.agents.imdb://tt0133093?lang=en", "tt0133093", "movie_results", MediaKind.Movie),
            (key, "com.plexapp.agents.thetvdb://2000?lang=en", "2000", "tv_results", MediaKind.Show));
        await AgeLibraryAsync(factory, LegacyRetryAfter);
        string failing;
        string later;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            string[] order = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryItems.AsNoTracking()
                .Where(item => item.RatingKey == "101" || item.RatingKey == key)
                .OrderBy(item => item.Id)
                .Select(item => item.RatingKey)
                .ToArrayAsync();
            (failing, later) = (order[0], order[1]);
        }

        factory.Plex.Delay = request => request.Path == $"/library/metadata/{failing}"
            ? throw new InvalidOperationException("The server is down.")
            : Task.CompletedTask;
        DateTimeOffset before = factory.Time.GetUtcNow() - LegacyRetryAfter;

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().RetryUnresolvedAsync(CancellationToken.None);
        }

        Assert.NotNull(await TitleIdAsync(factory, later));
        Assert.Null(await TitleIdAsync(factory, failing));
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            DateTimeOffset stamped = (await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryItems.AsNoTracking()
                .SingleAsync(item => item.RatingKey == failing)).IndexedAt;
            Assert.True(stamped > before);
        }

        int reads = factory.Plex.Count("GET", $"/library/metadata/{failing}");
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().RetryUnresolvedAsync(CancellationToken.None);
        }

        Assert.Equal(reads, factory.Plex.Count("GET", $"/library/metadata/{failing}"));
    }

    [Fact]
    public async Task Legacy_retries_stop_once_tmdb_reports_that_no_key_is_configured()
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Show);
        await IndexUnresolvedAsync(
            factory,
            ("101", "com.plexapp.agents.imdb://tt0133093?lang=en", "tt0133093", "movie_results", MediaKind.Movie),
            (key, "com.plexapp.agents.thetvdb://2000?lang=en", "2000", "tv_results", MediaKind.Show));
        await AgeLibraryAsync(factory, LegacyRetryAfter);
        int reads = factory.Plex.Count("GET", "/library/metadata/101") + factory.Plex.Count("GET", $"/library/metadata/{key}");
        factory.Plex.Delay = request =>
        {
            if (request.Path.StartsWith("/library/metadata/", StringComparison.Ordinal))
            {
                factory.TmdbKeys.Key = null;
            }

            return Task.CompletedTask;
        };

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().RetryUnresolvedAsync(CancellationToken.None);
        }

        Assert.Equal(reads + 1, factory.Plex.Count("GET", "/library/metadata/101") + factory.Plex.Count("GET", $"/library/metadata/{key}"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_failure_after_the_write_was_sent_keeps_the_owner_so_the_retry_ends_labeled_and_owned(int kind)
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        factory.Services.GetRequiredService<TransportFailureMediaServer>().FailAfterNextWrite = kind switch
        {
            0 => new HttpRequestException("The connection closed before the answer arrived."),
            1 => new MediaServerException("Plex answered 502.", System.Net.HttpStatusCode.BadGateway),
            _ => new TaskCanceledException("The request timed out."),
        };
        await RunAsync(factory);

        Assert.Equal(RequestState.Available, (await factory.GetRequestsAsync()).Single().State);
        Assert.NotEmpty(factory.Plex.Item(key).Labels);
        OwnedLabel owner = Assert.Single(await factory.GetLedgerAsync());

        await RunAsync(factory);

        Assert.Equal(RequestState.Labeled, (await factory.GetRequestsAsync()).Single().State);
        Assert.Equal(1, factory.LabelWrites);
        Assert.Equal(owner.Id, Assert.Single(await factory.GetLedgerAsync()).Id);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Labeled);
    }

    [Fact]
    public async Task A_retryable_failure_on_a_copy_sharing_a_guid_keeps_the_earlier_copy_and_the_shared_row()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        string guid = factory.Plex.Item(key).Guid;
        factory.Plex.Rematch("102", guid, "Another copy", $"tmdb://{TmdbId(MediaKind.Movie)}");
        int puts = 0;
        factory.Plex.Delay = request => request.Method == "PUT" && Interlocked.Increment(ref puts) == 2
            ? throw new InvalidOperationException("The server is down.")
            : Task.CompletedTask;

        await RunAsync(factory);

        Assert.Equal(RequestState.Available, (await factory.GetRequestsAsync()).Single().State);
        Assert.Single(await factory.GetLedgerAsync());
        Assert.Single(new[] { key, "102" }, item => factory.Plex.Item(item).Labels.Count > 0);

        factory.Plex.Delay = null;
        await RunAsync(factory);

        Assert.Equal(RequestState.Labeled, (await factory.GetRequestsAsync()).Single().State);
        Assert.NotEmpty(factory.Plex.Item(key).Labels);
        Assert.Equal(factory.Plex.Item(key).Labels, factory.Plex.Item("102").Labels);
        Assert.Single(await factory.GetLedgerAsync());
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Labeled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rolling_back_an_earlier_copy_keeps_the_shared_row_when_a_later_copys_write_was_sent(bool changedItem)
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        string guid = factory.Plex.Item(key).Guid;
        factory.Plex.Rematch("102", guid, "Another copy", $"tmdb://{TmdbId(MediaKind.Movie)}");
        if (changedItem)
        {
            int puts = 0;
            factory.Plex.AfterRequest = request =>
            {
                if (request.Method == "PUT" && Interlocked.Increment(ref puts) == 2)
                {
                    factory.Plex.Rematch(request.Query["id"], guid, "Replacement movie", "tmdb://700");
                }
            };
        }
        else
        {
            TransportFailureMediaServer plex = factory.Services.GetRequiredService<TransportFailureMediaServer>();
            plex.WritesBeforeFailure = 1;
            plex.FailAfterNextWrite = new OperationCanceledException();
        }

        await RunAsync(factory);

        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.Single(await factory.GetLedgerAsync());
        Assert.Single(new[] { key, "102" }, item => factory.Plex.Item(item).Labels.Count > 0);
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Labeled);
    }

    [Fact]
    public async Task Rolling_back_a_copy_forgets_its_row_when_the_failing_copy_has_another_guid()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        factory.Plex.Rematch("102", "com.plexapp.agents.imdb://tt0133093?lang=en", "Legacy copy", $"tmdb://{TmdbId(MediaKind.Movie)}");
        TransportFailureMediaServer plex = factory.Services.GetRequiredService<TransportFailureMediaServer>();
        plex.WritesBeforeFailure = 1;
        plex.FailAfterNextWrite = new OperationCanceledException();

        await RunAsync(factory);

        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        string labeled = Assert.Single(new[] { key, "102" }, item => factory.Plex.Item(item).Labels.Count > 0);
        Assert.Equal(factory.Plex.Item(labeled).Guid, Assert.Single(await factory.GetLedgerAsync()).Guid);
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Labeled);
    }

    [Fact]
    public async Task Rolling_back_a_copy_keeps_the_shared_row_when_an_earlier_attempt_labeled_another_copy_of_the_guid()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        string guid = factory.Plex.Item(key).Guid;
        string[] order = await SeedCopiesAsync(factory, key);
        int puts = 0;
        int thirdReads = 0;
        factory.Plex.Delay = request =>
        {
            if (request.Method == "PUT" && Interlocked.Increment(ref puts) == 2)
            {
                throw new InvalidOperationException("The server is down.");
            }

            if (request.Path == $"/library/metadata/{order[2]}" && Interlocked.Increment(ref thirdReads) == 3)
            {
                factory.Plex.Rematch(order[2], guid, "Replacement movie", "tmdb://700");
            }

            return Task.CompletedTask;
        };

        await RunAsync(factory);
        Assert.Equal(RequestState.Available, (await factory.GetRequestsAsync()).Single().State);
        Assert.NotEmpty(factory.Plex.Item(order[0]).Labels);
        Assert.Empty(factory.Plex.Item(order[1]).Labels);

        await RunAsync(factory);

        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.NotEmpty(factory.Plex.Item(order[0]).Labels);
        Assert.Empty(factory.Plex.Item(order[1]).Labels);
        Assert.Single(await factory.GetLedgerAsync());
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Labeled);
    }

    [Fact]
    public async Task Rolling_back_two_copies_of_one_guid_removes_both_labels_before_the_row_is_forgotten()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        string[] order = await SeedSharedPairWithFailingThirdAsync(factory, key);

        await RunAsync(factory);

        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.All(order, copy => Assert.Empty(factory.Plex.Item(copy).Labels));
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Labeled);
    }

    [Fact]
    public async Task A_failed_removal_of_one_copy_keeps_the_row_its_sibling_copy_still_needs()
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        string[] order = await SeedSharedPairWithFailingThirdAsync(factory, key);
        int puts = 0;
        Func<Wishlistarr.Tests.Plex.FakePlexServer.RecordedRequest, Task> rematchThird = factory.Plex.Delay!;
        factory.Plex.Delay = request => request.Method == "PUT" && Interlocked.Increment(ref puts) == 3
            ? throw new InvalidOperationException("The server is down.")
            : rematchThird(request);

        await RunAsync(factory);

        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.NotEmpty(factory.Plex.Item(order[0]).Labels);
        Assert.Empty(factory.Plex.Item(order[1]).Labels);
        Assert.Single(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task A_hand_added_label_on_a_sibling_copy_does_not_keep_a_row_this_call_created_after_the_only_label_it_covered_was_removed()
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        string[] order = await SeedSharedPairWithFailingThirdAsync(factory, key);
        string label;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            label = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().KidProfiles.Select(row => row.PlexLabel).SingleAsync();
        }

        factory.Plex.Item(order[1]).SetLabels(label);

        await RunAsync(factory);

        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.Empty(factory.Plex.Item(order[0]).Labels);
        Assert.Equal([label], factory.Plex.Item(order[1]).Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Rolling_back_a_copy_never_forgets_a_row_that_existed_for_a_copy_outside_the_request()
    {
        using ArrivalApiFactory factory = new();
        (_, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        string guid = factory.Plex.Item(key).Guid;
        factory.Plex.Rematch("102", guid, "Second copy", $"tmdb://{TmdbId(MediaKind.Movie)}");
        factory.Plex.AddItem(new Wishlistarr.Tests.Plex.FakePlexServer.FakeItem("103", "movie", guid, "Outside copy", 1) { GuidIds = ["tmdb://999"] });
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().FullAsync(CancellationToken.None);
        }

        string[] order = await CopyOrderAsync(factory);
        factory.Plex.Rematch(order[1], "plex://movie/other", "Other copy", $"tmdb://{TmdbId(MediaKind.Movie)}");
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().FullAsync(CancellationToken.None);
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            KidProfile profile = await db.KidProfiles.AsNoTracking().SingleAsync();
            await scope.ServiceProvider.GetRequiredService<LabelWriter>().AddAsync("103", profile.PlexLabel, profile.Id, "grant");
        }

        int reads = 0;
        factory.Plex.Delay = request =>
        {
            if (request.Path == $"/library/metadata/{order[1]}" && Interlocked.Increment(ref reads) == 2)
            {
                factory.Plex.Rematch(order[1], "plex://movie/other", "Replacement movie", "tmdb://700");
            }

            return Task.CompletedTask;
        };

        await RunAsync(factory);

        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.Empty(factory.Plex.Item(order[0]).Labels);
        Assert.NotEmpty(factory.Plex.Item("103").Labels);
        Assert.Equal(guid, Assert.Single(await factory.GetLedgerAsync()).Guid);
    }

    [Fact]
    public async Task An_available_request_whose_title_stays_missing_fails_after_the_window()
    {
        using ArrivalApiFactory factory = new();
        (_, _, int id, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        Arrive(factory, key, MediaKind.Movie);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaRequests>().TransitionAsync(id, RequestState.Available, RequestActor.System(RequestArrival.Component));
        }

        factory.Plex.DeleteItem(key);
        await RunAsync(factory);
        Assert.Equal(RequestState.Available, (await factory.GetRequestsAsync()).Single().State);

        await AgeRequestAsync(factory, TimeSpan.FromHours(23));
        await RunAsync(factory);
        Assert.Equal(RequestState.Available, (await factory.GetRequestsAsync()).Single().State);

        await AgeRequestAsync(factory, TimeSpan.FromHours(24));
        await RunAsync(factory);

        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.Equal(RequestLabeling.NoLongerInLibraryReason, (await factory.GetHistoryAsync(id)).Single(item => item.ToState == RequestState.Failed).Reason);
        Assert.Equal(0, factory.LabelWrites);
    }

    [Fact]
    public async Task Only_arrival_webhook_events_start_the_arrival_job()
    {
        using ArrivalApiFactory factory = new();
        (HttpClient parent, _, _, string key) = await DispatchAsync(factory, "arr", MediaKind.Movie);
        JobRunner jobs = factory.Services.GetRequiredService<JobRunner>();
        await WaitForJobAsync(factory, JobRunOutcome.Succeeded);
        int baseline = (await jobs.GetLastRunAsync(RequestArrivalJob.JobName))!.Id;
        (string Source, string Event)[] ignored = [("sonarr", "Grab"), ("radarr", "Grab"), ("seerr", "MEDIA_PENDING"), ("plex", "media.play")];
        for (int index = 0; index < ignored.Length; index++)
        {
            await SendWebhookAsync(factory, parent, ignored[index].Source, key, index, ignored[index].Event);
        }

        Assert.False(jobs.IsRunning(RequestArrivalJob.JobName));
        Assert.Equal(baseline, (await jobs.GetLastRunAsync(RequestArrivalJob.JobName))!.Id);

        (string Source, string Event)[] arrivals = [("sonarr", "Download"), ("radarr", "Download"), ("seerr", "MEDIA_AVAILABLE"), ("plex", "library.new")];
        int previous = baseline;
        for (int index = 0; index < arrivals.Length; index++)
        {
            await SendWebhookAsync(factory, parent, arrivals[index].Source, key, 100 + index, arrivals[index].Event);
            await WaitForJobAsync(factory, JobRunOutcome.Succeeded);
            JobRun run = (await jobs.GetLastRunAsync(RequestArrivalJob.JobName))!;
            Assert.Equal(JobRunTrigger.Webhook, run.Trigger);
            Assert.True(run.Id > previous);
            previous = run.Id;
        }
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

    private static async Task IndexUnresolvedAsync(ArrivalApiFactory factory, params (string Key, string Guid, string ExternalId, string Results, MediaKind Kind)[] items)
    {
        foreach ((string key, string guid, string externalId, string results, MediaKind kind) in items)
        {
            factory.Plex.Rematch(key, guid, "Legacy title");
            string path = $"/3/find/{externalId}";
            factory.Tmdb.SetBody(path, $$"""{"{{results}}":[{"id":{{TmdbId(kind)}}}]}""");
            factory.Tmdb.Enqueue(path, 503);
        }

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().FullAsync(CancellationToken.None);
        }

        foreach ((string key, _, string externalId, _, _) in items)
        {
            Assert.Null(await TitleIdAsync(factory, key));
            Assert.Equal(1, factory.Tmdb.Count($"/3/find/{externalId}"));
        }
    }

    private static async Task<int?> TitleIdAsync(ArrivalApiFactory factory, string key)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryItems.AsNoTracking()
            .SingleAsync(item => item.RatingKey == key)).TitleId;
    }

    private static async Task AgeLibraryAsync(ArrivalApiFactory factory, TimeSpan age)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        DateTimeOffset stamp = factory.Time.GetUtcNow() - age;
        await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryItems
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IndexedAt, stamp));
    }

    private static async Task AgeRequestAsync(ArrivalApiFactory factory, TimeSpan age)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        DateTimeOffset stamp = factory.Time.GetUtcNow() - age;
        await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().MediaRequests
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.StateChangedAt, stamp));
    }

    private static async Task RetryWhileGateHeldAsync(ArrivalApiFactory factory)
    {
        using IDisposable held = await factory.Services.GetRequiredService<LibraryRefreshGate>().EnterAsync(CancellationToken.None);
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().RetryUnresolvedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<string[]> CopyOrderAsync(ArrivalApiFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IEnumerable<MediaItem> copies = await scope.ServiceProvider.GetRequiredService<LibraryLookup>()
            .FindAsync(new TitleKey(MediaKind.Movie, TmdbId(MediaKind.Movie)), CancellationToken.None);
        return [.. copies.Select(copy => copy.Id)];
    }

    private static async Task<string[]> SeedCopiesAsync(ArrivalApiFactory factory, string key)
    {
        string guid = factory.Plex.Item(key).Guid;
        string tmdb = $"tmdb://{TmdbId(MediaKind.Movie)}";
        factory.Plex.Rematch("102", guid, "Second copy", tmdb);
        factory.Plex.AddItem(new Wishlistarr.Tests.Plex.FakePlexServer.FakeItem("103", "movie", guid, "Third copy", 1) { GuidIds = [tmdb] });
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().FullAsync(CancellationToken.None);
        }

        string[] order = await CopyOrderAsync(factory);
        Assert.Equal(3, order.Length);
        return order;
    }

    private static async Task<string[]> SeedSharedPairWithFailingThirdAsync(ArrivalApiFactory factory, string key)
    {
        string[] order = await SeedCopiesAsync(factory, key);
        factory.Plex.Rematch(order[2], "plex://movie/other", "Other copy", $"tmdb://{TmdbId(MediaKind.Movie)}");
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LibraryRefresh>().FullAsync(CancellationToken.None);
        }

        int reads = 0;
        factory.Plex.Delay = request =>
        {
            if (request.Path == $"/library/metadata/{order[2]}" && Interlocked.Increment(ref reads) == 2)
            {
                factory.Plex.Rematch(order[2], "plex://movie/other", "Replacement movie", "tmdb://700");
            }

            return Task.CompletedTask;
        };

        return order;
    }

    private static int TmdbId(MediaKind kind) => kind == MediaKind.Movie ? DispatchApiFactory.MovieId : FakeArrServer.ShowTmdbId;

    private static async Task<(HttpClient Parent, HttpClient Kid, int Id, string Key)> DispatchAsync(ArrivalApiFactory factory, string mode, MediaKind kind)
    {
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

    private static async Task SendWebhookAsync(ArrivalApiFactory factory, HttpClient parent, string source, string key, int delivery, string? eventType = null)
    {
        JsonElement endpoints = await parent.GetFromJsonAsync<JsonElement>("/api/v1/settings/webhooks");
        string url = endpoints.EnumerateArray().Single(item => item.GetProperty("source").GetString() == source).GetProperty("url").GetString()!;
        string body = source switch
        {
            "plex" => $$"""{"event":"{{eventType ?? "library.new"}}","Metadata":{"ratingKey":"{{key}}","type":"show"},"delivery":{{delivery}}}""",
            "seerr" => $$"""{"notification_type":"{{eventType ?? "MEDIA_AVAILABLE"}}","media":{"media_type":"movie","tmdbId":"603"},"delivery":{{delivery}}}""",
            "sonarr" => $$"""{"eventType":"{{eventType ?? "Download"}}","series":{"id":1,"tvdbId":2000},"episodes":[{"seasonNumber":1,"episodeNumber":1}],"delivery":{{delivery}}}""",
            _ => $$"""{"eventType":"{{eventType ?? "Download"}}","movie":{"id":1,"tmdbId":603},"delivery":{{delivery}}}""",
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

internal sealed class TransportFailureMediaServer(PlexMediaServer inner) : IMediaServer, ILibraryScanner
{
    public Exception? FailAfterNextWrite { get; set; }

    public int WritesBeforeFailure { get; set; }

    private int writes;

    public Exception? GetEntryFailure { get; set; }

    public int GetEntryCalls { get; private set; }

    public Task<LibraryScan> ScanAsync(DateTimeOffset? updatedSince, CancellationToken ct = default) => inner.ScanAsync(updatedSince, ct);

    public Task<LibraryLabelScan> ScanLabelsAsync(CancellationToken ct = default) => inner.ScanLabelsAsync(ct);

    public Task<string?> GetServerIdAsync(CancellationToken ct = default) => inner.GetServerIdAsync(ct);

    public Task<LibrarySnapshot?> GetEntryAsync(string itemId, CancellationToken ct = default)
    {
        this.GetEntryCalls++;
        return this.GetEntryFailure is Exception failure ? throw failure : inner.GetEntryAsync(itemId, ct);
    }

    public Task<MediaServerTestResult> TestConnectionAsync(CancellationToken ct = default) => inner.TestConnectionAsync(ct);

    public Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct = default) => inner.GetLibrariesAsync(ct);

    public Task<IReadOnlyList<MediaServerUser>> GetUsersAsync(CancellationToken ct = default) => inner.GetUsersAsync(ct);

    public Task<IReadOnlyList<MediaItem>> FindByExternalIdAsync(MediaKind kind, ExternalId id, CancellationToken ct = default) => inner.FindByExternalIdAsync(kind, id, ct);

    public Task<IReadOnlyList<MediaItem>> FindByGuidAsync(string guid, CancellationToken ct = default) => inner.FindByGuidAsync(guid, ct);

    public Task<MediaItemLabels?> GetLabelsAsync(string itemId, CancellationToken ct = default) => inner.GetLabelsAsync(itemId, ct);

    public async Task SetLabelsAsync(MediaItemLabels item, IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        await inner.SetLabelsAsync(item, labels, ct);
        if (this.FailAfterNextWrite is Exception failure && this.writes++ >= this.WritesBeforeFailure)
        {
            this.FailAfterNextWrite = null;
            throw failure;
        }
    }
}
