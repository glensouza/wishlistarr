using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Tests.Arr;
using Wishlistarr.Tests.Seerr;

namespace Wishlistarr.Tests.Requests;

public sealed class RequestDispatchTests
{
    [Theory]
    [InlineData("arr")]
    [InlineData("seerr")]
    public async Task A_season_request_only_dispatches_the_selected_seasons(string mode)
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync(mode);
        int id;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            int tmdbId = mode == "arr" ? DispatchApiFactory.ShowId : FakeSeerrServer.ShowTmdbId;
            RequestResult made = await scope.ServiceProvider.GetRequiredService<MediaRequests>().CreateAsync(
                profile, new TitleKey(MediaKind.Show, tmdbId), "Fixture show", RequestActor.Kid(profile, "Ada"), seasons: [2]);
            id = made.Request!.Id;
        }

        await factory.ApproveAsync(parent, id);
        await factory.WaitForDispatchAsync(id);
        if (mode == "seerr")
        {
            FakeSeerrServer.RecordedRequest post = Assert.Single(factory.Seerr.Sent("POST", "/api/v1/request"));
            Assert.Equal([2], post.Body!["seasons"]!.AsArray().Select(season => season!.GetValue<int>()));
        }
        else
        {
            JsonObject show = Assert.Single(factory.Sonarr.Library);
            Assert.Equal([2], show["seasons"]!.AsArray().Where(season => season!["monitored"]!.GetValue<bool>()).Select(season => season!["seasonNumber"]!.GetValue<int>()));
        }
    }

    [Fact]
    public async Task An_approved_movie_is_sent_to_seerr_as_the_configured_user()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("seerr");
        int id = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);

        await factory.ApproveAsync(parent, id);
        MediaRequest sent = await factory.WaitForAsync(id, RequestState.Fulfilling);

        Assert.Equal(FulfilmentBackend.Seerr, sent.Backend);
        Assert.NotNull(sent.ExternalId);
        Assert.NotNull(sent.DispatchedAt);
        FakeSeerrServer.RecordedRequest post = Assert.Single(factory.Seerr.Sent("POST", "/api/v1/request"));
        Assert.Equal(FakeSeerrServer.DefaultUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), post.ApiUser);
        Assert.Equal(DispatchApiFactory.MovieId, post.Body!["mediaId"]!.GetValue<int>());
        Assert.Empty(factory.Radarr.Library);
        Assert.Empty(factory.Sonarr.Library);
        List<MediaRequestEvent> history = await factory.GetHistoryAsync(id);
        MediaRequestEvent moved = history.Last();
        Assert.Equal(RequestState.Fulfilling, moved.ToState);
        Assert.Equal(RequestActorKind.System, moved.ActorKind);
        Assert.Equal(RequestDispatch.Component, moved.ActorName);
    }

    [Fact]
    public async Task An_approved_show_is_sent_to_seerr_for_every_season()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("seerr");
        int id = await factory.CreateRequestAsync(profile, MediaKind.Show, FakeSeerrServer.ShowTmdbId);

        await factory.ApproveAsync(parent, id);
        await factory.WaitForAsync(id, RequestState.Fulfilling);

        FakeSeerrServer.RecordedRequest post = Assert.Single(factory.Seerr.Sent("POST", "/api/v1/request"));
        Assert.Equal("tv", post.Body!["mediaType"]!.GetValue<string>());
        Assert.Equal("all", post.Body["seasons"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_approved_movie_goes_to_radarr_and_a_show_to_sonarr()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        int movie = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);
        int show = await factory.CreateRequestAsync(profile, MediaKind.Show, DispatchApiFactory.ShowId);

        await factory.ApproveAsync(parent, movie);
        await factory.ApproveAsync(parent, show);
        MediaRequest movieRow = await factory.WaitForAsync(movie, RequestState.Fulfilling);
        MediaRequest showRow = await factory.WaitForAsync(show, RequestState.Fulfilling);

        Assert.Equal(FulfilmentBackend.Arr, movieRow.Backend);
        Assert.Equal(FulfilmentBackend.Arr, showRow.Backend);
        Assert.Equal(factory.Radarr.Library.Single()["id"]!.GetValue<int>(), movieRow.ExternalId);
        Assert.Equal(factory.Sonarr.Library.Single()["id"]!.GetValue<int>(), showRow.ExternalId);
        Assert.Empty(factory.Seerr.Sent("POST", "/api/v1/request"));
    }

    [Fact]
    public async Task A_title_the_backend_already_has_is_attached_and_never_added()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        JsonObject seeded = factory.Radarr.SeedMovie(monitored: true, hasFile: true);
        int id = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);

        await factory.ApproveAsync(parent, id);
        MediaRequest sent = await factory.WaitForAsync(id, RequestState.Fulfilling);

        Assert.Equal(seeded["id"]!.GetValue<int>(), sent.ExternalId);
        Assert.Empty(factory.Radarr.Sent("POST", "/api/v3/movie"));
        Assert.Single(factory.Radarr.Library);
    }

    [Fact]
    public async Task A_request_keeps_its_backend_when_the_mode_changes_afterwards()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        factory.Radarr.FailWith = StatusCodes.Status503ServiceUnavailable;
        int inFlight = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);
        await factory.ApproveAsync(parent, inFlight);
        await factory.WaitForAsync(inFlight, RequestState.Failed);

        await factory.SetModeAsync(parent, "seerr");
        factory.Radarr.FailWith = null;
        int later = await factory.CreateRequestAsync(profile, MediaKind.Show, DispatchApiFactory.ShowId);
        await factory.ApproveAsync(parent, later);
        JsonElement retried = await factory.RetryAsync(parent, inFlight);
        MediaRequest old = await factory.WaitForDispatchAsync(inFlight);
        MediaRequest fresh = await factory.WaitForAsync(later, RequestState.Fulfilling);

        Assert.Equal("succeeded", retried.GetProperty("status").GetString());
        Assert.Equal(FulfilmentBackend.Arr, old.Backend);
        Assert.Single(factory.Radarr.Library);
        Assert.Equal(FulfilmentBackend.Seerr, fresh.Backend);
        FakeSeerrServer.RecordedRequest post = Assert.Single(factory.Seerr.Sent("POST", "/api/v1/request"));
        Assert.Equal("tv", post.Body!["mediaType"]!.GetValue<string>());
        Assert.Empty(factory.Sonarr.Library);
    }

    [Fact]
    public async Task A_request_approved_but_not_yet_sent_goes_where_it_was_approved_when_the_mode_changes()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        int id = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RequestResult approved = await scope.ServiceProvider.GetRequiredService<MediaRequests>()
                .TransitionAsync(id, RequestState.Approved, RequestActor.Parent(null, "Parent"));
            Assert.Equal(FulfilmentBackend.Arr, approved.Request!.Backend);
        }

        await factory.SetModeAsync(parent, "seerr");
        await parent.PostAsync($"/api/v1/jobs/{RequestDispatchJob.JobName}/run", null);
        MediaRequest sent = await factory.WaitForDispatchAsync(id);

        Assert.Equal(FulfilmentBackend.Arr, sent.Backend);
        Assert.Single(factory.Radarr.Library);
        Assert.Empty(factory.Seerr.Sent("POST", "/api/v1/request"));
    }

    [Fact]
    public async Task A_request_approved_before_a_mode_was_chosen_is_sent_once_one_is()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync(null);
        int id = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);

        await factory.ApproveAsync(parent, id);
        await factory.SetModeAsync(parent, "arr");
        await parent.PostAsync($"/api/v1/jobs/{RequestDispatchJob.JobName}/run", null);
        MediaRequest sent = await factory.WaitForAsync(id, RequestState.Fulfilling);

        Assert.Equal(FulfilmentBackend.Arr, sent.Backend);
        Assert.Single(factory.Radarr.Library);
    }

    [Theory]
    [InlineData(ArrService.Radarr)]
    [InlineData(ArrService.Sonarr)]
    public async Task A_retry_after_a_lost_answer_does_not_add_the_title_twice(ArrService service)
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        FakeArrServer fake = service == ArrService.Radarr ? factory.Radarr : factory.Sonarr;
        fake.DropNextAddResponse = true;
        MediaKind kind = service == ArrService.Radarr ? MediaKind.Movie : MediaKind.Show;
        int tmdb = service == ArrService.Radarr ? DispatchApiFactory.MovieId : DispatchApiFactory.ShowId;
        int id = await factory.CreateRequestAsync(profile, kind, tmdb);

        await factory.ApproveAsync(parent, id);
        await factory.WaitForAsync(id, RequestState.Failed);
        Assert.Single(fake.Library);
        JsonElement retried = await factory.RetryAsync(parent, id);
        await factory.WaitForDispatchAsync(id);

        Assert.Equal("succeeded", retried.GetProperty("status").GetString());
        Assert.Single(fake.Library);
        Assert.Single(fake.Sent("POST", service == ArrService.Radarr ? "/api/v3/movie" : "/api/v3/series"));
    }

    [Fact]
    public async Task A_seerr_request_made_before_the_answer_was_lost_is_attached_on_retry()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("seerr");
        factory.Seerr.DropNextCreateResponse = true;
        int id = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);

        await factory.ApproveAsync(parent, id);
        await factory.WaitForAsync(id, RequestState.Failed);
        await factory.RetryAsync(parent, id);
        MediaRequest sent = await factory.WaitForDispatchAsync(id);

        Assert.Single(factory.Seerr.Sent("POST", "/api/v1/request"));
        Assert.NotNull(sent.ExternalId);
    }

    [Fact]
    public async Task A_backend_that_fails_leaves_the_request_failed_with_a_plain_reason_and_a_retry()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        factory.Radarr.FailWith = StatusCodes.Status503ServiceUnavailable;
        int id = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);

        await factory.ApproveAsync(parent, id);
        MediaRequest failed = await factory.WaitForAsync(id, RequestState.Failed);
        HttpResponseMessage listed = await parent.GetAsync("/api/v1/requests?state=failed");
        string text = await listed.Content.ReadAsStringAsync();
        JsonElement item = JsonDocument.Parse(text).RootElement.GetProperty("items")[0];
        MediaRequestEvent failure = (await factory.GetHistoryAsync(id)).Last();

        Assert.Null(failed.DispatchedAt);
        Assert.Equal(RequestState.Failed, failure.ToState);
        Assert.Equal(RequestState.Approved, failure.FromState);
        Assert.Equal(RequestDispatch.UnavailableReason, failure.Reason);
        Assert.Equal(RequestActorKind.System, failure.ActorKind);
        Assert.True(item.GetProperty("canRetry").GetBoolean());
        Assert.Equal(RequestDispatch.UnavailableCode, item.GetProperty("dispatchFailure").GetString());
        Assert.DoesNotContain(FakeArrServer.ApiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Radarr.Address.Host, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeArrServer.ApiKey, failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_connection_fails_the_request_as_not_set_up()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        await factory.SetModeAsync(parent, "seerr");
        int id = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);

        await factory.ApproveAsync(parent, id);
        await factory.WaitForAsync(id, RequestState.Failed);
        JsonElement item = (await factory.ListAsync(parent, "failed"))[0];

        Assert.Equal(RequestDispatch.NotConfiguredCode, item.GetProperty("dispatchFailure").GetString());
        Assert.Empty(factory.Seerr.Requests);
    }

    [Fact]
    public async Task A_failed_request_is_sent_again_when_the_parent_retries_after_fixing_the_backend()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        factory.Radarr.FailWith = StatusCodes.Status503ServiceUnavailable;
        int id = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);
        await factory.ApproveAsync(parent, id);
        await factory.WaitForAsync(id, RequestState.Failed);
        factory.Radarr.FailWith = null;

        JsonElement first = await factory.RetryAsync(parent, id);
        MediaRequest sent = await factory.WaitForDispatchAsync(id);
        JsonElement second = await factory.RetryAsync(parent, id);

        Assert.Equal("succeeded", first.GetProperty("status").GetString());
        Assert.Equal("alreadyDecided", second.GetProperty("status").GetString());
        Assert.NotNull(sent.DispatchedAt);
        Assert.Single(factory.Radarr.Library);
    }

    [Fact]
    public async Task Only_a_parent_can_retry()
    {
        using DispatchApiFactory factory = new();
        (HttpClient kid, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        factory.Radarr.FailWith = StatusCodes.Status503ServiceUnavailable;
        int id = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);
        await factory.ApproveAsync(parent, id);
        await factory.WaitForAsync(id, RequestState.Failed);
        factory.Radarr.FailWith = null;

        HttpResponseMessage asKid = await kid.PostAsJsonAsync("/api/v1/requests/retry", new { ids = new[] { id } });
        HttpResponseMessage anonymous = await factory.CreateClient().PostAsJsonAsync("/api/v1/requests/retry", new { ids = new[] { id } });

        Assert.Equal(HttpStatusCode.Forbidden, asKid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single().State);
        Assert.Empty(factory.Radarr.Library);
    }

    [Fact]
    public async Task Retry_checks_its_input_and_leaves_other_failures_to_revoke()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        int labelFailure = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);
        int pending = await factory.CreateRequestAsync(profile, MediaKind.Show, DispatchApiFactory.ShowId);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            MediaRequests requests = scope.ServiceProvider.GetRequiredService<MediaRequests>();
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            RequestActor system = RequestActor.System("test");
            await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
            await requests.TransitionAsync(labelFailure, RequestState.Approved, system);
            await requests.TransitionAsync(labelFailure, RequestState.Available, system);
            await requests.TransitionAsync(labelFailure, RequestState.Failed, system, RequestLabeling.WriteFailedReason);
            await transaction.CommitAsync();
        }

        HttpResponseMessage empty = await parent.PostAsJsonAsync("/api/v1/requests/retry", new { ids = Array.Empty<int>() });
        HttpResponseMessage negative = await parent.PostAsJsonAsync("/api/v1/requests/retry", new { ids = new[] { -1 } });
        HttpResponseMessage tooLong = await parent.PostAsJsonAsync("/api/v1/requests/retry", new { ids = new[] { labelFailure }, reason = new string('x', 501) });
        JsonElement labeling = await factory.RetryAsync(parent, labelFailure);
        JsonElement notFailed = await factory.RetryAsync(parent, pending);
        JsonElement missing = await factory.RetryAsync(parent, 9999);
        JsonElement listed = (await factory.ListAsync(parent, "failed"))[0];

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("conflict", labeling.GetProperty("status").GetString());
        Assert.Equal("conflict", notFailed.GetProperty("status").GetString());
        Assert.Equal("notFound", missing.GetProperty("status").GetString());
        Assert.False(listed.GetProperty("canRetry").GetBoolean());
        Assert.Equal(RequestState.Failed, (await factory.GetRequestsAsync()).Single(row => row.Id == labelFailure).State);
    }

    [Fact]
    public async Task A_grant_is_never_sent_to_the_backend_between_its_approval_and_its_move_to_available()
    {
        using DispatchApiFactory factory = new();
        factory.Index!.Add(MediaKind.Movie, DispatchApiFactory.MovieId, new MediaItem("101", "plex://movie/5d7768ba96b655001fdc0408", "The Matrix", 1999, MediaKind.Movie, "1", []));
        await factory.LinkPlexAsync();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync("arr");
        factory.Saves.AvailableGate = async () =>
        {
            using IServiceScope scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RequestDispatch>().DispatchPendingAsync(CancellationToken.None);
        };

        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = profile, kind = "movie", tmdbId = DispatchApiFactory.MovieId });
        factory.Saves.AvailableGate = null;
        MediaRequest granted = Assert.Single(await factory.GetRequestsAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(RequestState.Labeled, granted.State);
        Assert.Null(granted.DispatchedAt);
        Assert.Equal(1, factory.LabelWrites);
        Assert.Empty(factory.Radarr.Library);
        Assert.Empty(factory.Radarr.Sent("POST", "/api/v3/movie"));
    }

    [Fact]
    public async Task A_send_that_finishes_after_the_request_moved_on_records_nothing()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        await factory.ConfiguredParentAsync("seerr");
        int id = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaRequests>()
                .TransitionAsync(id, RequestState.Approved, RequestActor.Parent(null, "Parent"));
        }

        factory.Seerr.BeforeNextCreate = () =>
        {
            using IServiceScope scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<MediaRequests>()
                .TransitionAsync(id, RequestState.Available, RequestActor.System("test"))
                .GetAwaiter().GetResult();
        };
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<RequestDispatch>().DispatchPendingAsync(CancellationToken.None);
        }

        MediaRequest row = Assert.Single(await factory.GetRequestsAsync());

        Assert.Single(factory.Seerr.Sent("POST", "/api/v1/request"));
        Assert.Equal(RequestState.Available, row.State);
        Assert.Null(row.DispatchedAt);
        Assert.Null(row.ExternalId);
    }

    [Fact]
    public async Task A_retried_request_that_was_already_sent_is_sent_again()
    {
        using DispatchApiFactory factory = new();
        (_, int profile) = await factory.SignInKidAsync();
        HttpClient parent = await factory.ConfiguredParentAsync(null);
        int id = await factory.CreateRequestAsync(profile, MediaKind.Movie, DispatchApiFactory.MovieId);
        DateTimeOffset earlier = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            MediaRequests requests = scope.ServiceProvider.GetRequiredService<MediaRequests>();
            RequestActor system = RequestActor.System("test");
            await requests.TransitionAsync(id, RequestState.Approved, system);
            await requests.TransitionAsync(id, RequestState.Fulfilling, system);
            await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().MediaRequests
                .Where(row => row.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DispatchedAt, earlier));
            await requests.TransitionAsync(id, RequestState.Failed, system, "The download was removed.");
        }

        await factory.SetModeAsync(parent, "arr");
        JsonElement retried = await factory.RetryAsync(parent, id);
        MediaRequest sent = await factory.WaitForDispatchAsync(id);

        Assert.Equal("succeeded", retried.GetProperty("status").GetString());
        Assert.True(sent.DispatchedAt > earlier);
        Assert.Single(factory.Radarr.Library);
    }

    [Fact]
    public async Task The_fulfilment_mode_accepts_only_known_values()
    {
        using DispatchApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        HttpResponseMessage bad = await parent.PutAsJsonAsync("/api/v1/settings/fulfilment.mode", new { value = "plex" });

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        foreach (string mode in new[] { "seerr", "arr", "none" })
        {
            await factory.SetModeAsync(parent, mode);
        }
    }
}
