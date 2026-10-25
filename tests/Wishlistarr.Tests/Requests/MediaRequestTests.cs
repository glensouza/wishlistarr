using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Requests;

public sealed class RequestApiFactory : ApiFactory
{
    public static readonly TitleKey Matrix = new(MediaKind.Movie, 603);

    public static readonly RequestActor Parent = RequestActor.Parent(1, "parent");

    public static readonly RequestActor Dispatch = RequestActor.System("dispatch");

    public TestClock Clock { get; } = new();

    public async Task<int> CreateProfileAsync(string name, bool archived = false)
    {
        using IServiceScope scope = this.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        KidProfile profile = new()
        {
            Name = name,
            Avatar = "fox",
            PlexLabel = name,
            CreatedAt = this.Clock.GetUtcNow(),
            ArchivedAt = archived ? this.Clock.GetUtcNow() : null,
        };
        db.KidProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    public async Task<T> WithRequestsAsync<T>(Func<MediaRequests, Task<T>> action)
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<MediaRequests>());
    }

    public Task<RequestResult> CreateAsync(int kidProfileId, TitleKey? title = null, RequestActor? actor = null) =>
        this.WithRequestsAsync(requests => requests.CreateAsync(
            kidProfileId,
            title ?? Matrix,
            "The Matrix",
            actor ?? RequestActor.Kid(kidProfileId, "Ada")));

    public Task<RequestResult> MoveAsync(int requestId, RequestState to, RequestActor actor, string? reason = null) =>
        this.WithRequestsAsync(requests => requests.TransitionAsync(requestId, to, actor, reason));

    public Task<RequestResult> RetryAsync(int requestId, RequestActor actor) =>
        this.WithRequestsAsync(requests => requests.RetryAsync(requestId, actor));

    public Task<IReadOnlyList<MediaRequestEvent>> GetHistoryAsync(int requestId) =>
        this.WithRequestsAsync(requests => requests.GetHistoryAsync(requestId));

    public async Task<List<MediaRequest>> GetRequestsAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().MediaRequests
            .AsNoTracking()
            .OrderBy(request => request.Id)
            .ToListAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(this.Clock);
        });
    }
}

public sealed class MediaRequestTests
{
    [Fact]
    public async Task New_requests_are_pending_and_record_who_asked_and_when()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        factory.Clock.Advance(TimeSpan.FromMinutes(3));

        RequestResult result = await factory.CreateAsync(ada);

        Assert.Equal(RequestOutcome.Created, result.Outcome);
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(ada, request.KidProfileId);
        Assert.Equal(MediaKind.Movie, request.Kind);
        Assert.Equal(603, request.TmdbId);
        Assert.Equal("The Matrix", request.Title);
        Assert.Equal(RequestState.Pending, request.State);
        Assert.Equal(1, request.RequestCount);
        Assert.Equal(factory.Clock.GetUtcNow(), request.CreatedAt);
        MediaRequestEvent created = Assert.Single(await factory.GetHistoryAsync(request.Id));
        Assert.Null(created.FromState);
        Assert.Equal(RequestState.Pending, created.ToState);
        Assert.Equal(RequestActorKind.Kid, created.ActorKind);
        Assert.Equal(ada, created.ActorId);
        Assert.Equal("Ada", created.ActorName);
        Assert.Equal(factory.Clock.GetUtcNow(), created.OccurredAt);
    }

    [Fact]
    public async Task Every_transition_is_stored_with_time_and_actor()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int id = (await factory.CreateAsync(ada)).Request!.Id;
        DateTimeOffset start = factory.Clock.GetUtcNow();

        (RequestState To, RequestActor Actor)[] steps =
        [
            (RequestState.Approved, RequestApiFactory.Parent),
            (RequestState.Fulfilling, RequestApiFactory.Dispatch),
            (RequestState.Available, RequestActor.System("arrival")),
            (RequestState.Labeled, RequestActor.System("labels")),
        ];
        foreach ((RequestState to, RequestActor actor) in steps)
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(RequestOutcome.Changed, (await factory.MoveAsync(id, to, actor, $"to {to}")).Outcome);
        }

        IReadOnlyList<MediaRequestEvent> history = await factory.GetHistoryAsync(id);
        Assert.Equal(5, history.Count);
        RequestState previous = RequestState.Pending;
        for (int index = 0; index < steps.Length; index++)
        {
            MediaRequestEvent item = history[index + 1];
            Assert.Equal(previous, item.FromState);
            Assert.Equal(steps[index].To, item.ToState);
            Assert.Equal(steps[index].Actor.Kind, item.ActorKind);
            Assert.Equal(steps[index].Actor.Id, item.ActorId);
            Assert.Equal(steps[index].Actor.Name, item.ActorName);
            Assert.Equal($"to {steps[index].To}", item.Reason);
            Assert.Equal(start + TimeSpan.FromMinutes(index + 1), item.OccurredAt);
            previous = steps[index].To;
        }

        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Labeled, request.State);
        Assert.Equal(start + TimeSpan.FromMinutes(4), request.StateChangedAt);
    }

    [Theory]
    [InlineData(RequestState.Labeled)]
    [InlineData(RequestState.Fulfilling)]
    [InlineData(RequestState.Failed)]
    public async Task Illegal_transitions_are_rejected_and_leave_no_trace(RequestState to)
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int id = (await factory.CreateAsync(ada)).Request!.Id;

        RequestResult result = await factory.MoveAsync(id, to, RequestApiFactory.Dispatch);

        Assert.Equal(RequestOutcome.Rejected, result.Outcome);
        Assert.Equal($"A request cannot move from Pending to {to}.", result.Error);
        Assert.Equal(RequestState.Pending, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Single(await factory.GetHistoryAsync(id));
    }

    [Fact]
    public async Task Final_states_cannot_be_left()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int id = (await factory.CreateAsync(ada)).Request!.Id;
        await factory.MoveAsync(id, RequestState.Denied, RequestApiFactory.Parent);

        RequestResult result = await factory.MoveAsync(id, RequestState.Approved, RequestApiFactory.Parent);

        Assert.Equal(RequestOutcome.Rejected, result.Outcome);
        Assert.Equal(RequestState.Denied, Assert.Single(await factory.GetRequestsAsync()).State);
    }

    [Fact]
    public async Task Actors_outside_a_transition_are_rejected()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int id = (await factory.CreateAsync(ada)).Request!.Id;

        RequestResult kid = await factory.MoveAsync(id, RequestState.Approved, RequestActor.Kid(ada, "Ada"));
        await factory.MoveAsync(id, RequestState.Approved, RequestApiFactory.Parent);
        RequestResult parent = await factory.MoveAsync(id, RequestState.Fulfilling, RequestApiFactory.Parent);

        Assert.Equal(RequestOutcome.Rejected, kid.Outcome);
        Assert.Equal("A Kid cannot move a request from Pending to Approved.", kid.Error);
        Assert.Equal(RequestOutcome.Rejected, parent.Outcome);
        Assert.Equal(RequestState.Approved, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(2, (await factory.GetHistoryAsync(id)).Count);
    }

    [Fact]
    public async Task Repeating_a_transition_changes_nothing()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int id = (await factory.CreateAsync(ada)).Request!.Id;

        RequestResult first = await factory.MoveAsync(id, RequestState.Approved, RequestApiFactory.Parent);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        RequestResult second = await factory.MoveAsync(id, RequestState.Approved, RequestApiFactory.Parent);

        Assert.Equal(RequestOutcome.Changed, first.Outcome);
        Assert.Equal(RequestOutcome.Unchanged, second.Outcome);
        Assert.Equal(RequestState.Approved, second.Request!.State);
        Assert.Equal(2, (await factory.GetHistoryAsync(id)).Count);
        Assert.Equal(1, Assert.Single(await factory.GetRequestsAsync()).Version);
    }

    [Fact]
    public async Task Unknown_requests_are_not_found()
    {
        using RequestApiFactory factory = new();

        RequestResult moved = await factory.MoveAsync(42, RequestState.Approved, RequestApiFactory.Parent);
        RequestResult retried = await factory.RetryAsync(42, RequestApiFactory.Parent);

        Assert.Equal(RequestOutcome.NotFound, moved.Outcome);
        Assert.Equal(RequestOutcome.NotFound, retried.Outcome);
    }

    [Fact]
    public async Task Duplicate_requests_for_the_same_title_and_profile_are_merged()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int ben = await factory.CreateProfileAsync("Ben");
        RequestResult first = await factory.CreateAsync(ada);
        await factory.MoveAsync(first.Request!.Id, RequestState.Approved, RequestApiFactory.Parent);
        factory.Clock.Advance(TimeSpan.FromHours(1));

        RequestResult again = await factory.CreateAsync(ada);
        RequestResult byParent = await factory.CreateAsync(ada, actor: RequestApiFactory.Parent);
        RequestResult otherKid = await factory.CreateAsync(ben, actor: RequestActor.Kid(ben, "Ben"));
        RequestResult show = await factory.CreateAsync(ada, new TitleKey(MediaKind.Show, 603));

        Assert.Equal(RequestOutcome.Merged, again.Outcome);
        Assert.Equal(first.Request.Id, again.Request!.Id);
        Assert.Equal(RequestState.Approved, again.Request.State);
        Assert.Equal(RequestOutcome.Merged, byParent.Outcome);
        Assert.Equal(3, byParent.Request!.RequestCount);
        Assert.Equal(factory.Clock.GetUtcNow(), byParent.Request.LastRequestedAt);
        Assert.Equal(RequestOutcome.Created, otherKid.Outcome);
        Assert.Equal(RequestOutcome.Created, show.Outcome);
        Assert.Equal(3, (await factory.GetRequestsAsync()).Count);
        Assert.Equal(2, (await factory.GetHistoryAsync(first.Request.Id)).Count);
    }

    [Fact]
    public async Task Asking_again_after_a_denial_starts_a_new_request()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int denied = (await factory.CreateAsync(ada)).Request!.Id;
        await factory.MoveAsync(denied, RequestState.Denied, RequestApiFactory.Parent, "Too scary");

        RequestResult again = await factory.CreateAsync(ada);
        RequestResult merged = await factory.CreateAsync(ada);

        Assert.Equal(RequestOutcome.Created, again.Outcome);
        Assert.NotEqual(denied, again.Request!.Id);
        Assert.Equal(RequestOutcome.Merged, merged.Outcome);
        Assert.Equal(again.Request.Id, merged.Request!.Id);
        Assert.Equal([RequestState.Denied, RequestState.Pending], (await factory.GetRequestsAsync()).Select(request => request.State));
    }

    [Fact]
    public async Task Kids_can_only_request_for_their_own_active_profile()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int ben = await factory.CreateProfileAsync("Ben");
        int gone = await factory.CreateProfileAsync("Cy", archived: true);

        RequestResult forBen = await factory.CreateAsync(ben, actor: RequestActor.Kid(ada, "Ada"));
        RequestResult forArchived = await factory.CreateAsync(gone, actor: RequestApiFactory.Parent);

        Assert.Equal(RequestOutcome.Rejected, forBen.Outcome);
        Assert.Equal(RequestOutcome.Rejected, forArchived.Outcome);
        Assert.Empty(await factory.GetRequestsAsync());
    }

    [Fact]
    public async Task Malformed_input_is_refused()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int id = (await factory.CreateAsync(ada)).Request!.Id;

        await Assert.ThrowsAsync<ArgumentException>(() => factory.CreateAsync(ada, new TitleKey(MediaKind.Movie, 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => factory.WithRequestsAsync(requests =>
            requests.CreateAsync(ada, RequestApiFactory.Matrix, " ", RequestApiFactory.Parent)));
        await Assert.ThrowsAsync<ArgumentException>(() => factory.MoveAsync(id, RequestState.Approved, RequestActor.System("")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            factory.MoveAsync(id, RequestState.Approved, RequestApiFactory.Parent, new string('x', MediaRequests.MaxReasonLength + 1)));
    }

    [Fact]
    public async Task Failed_requests_retry_the_step_that_failed()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int dispatch = (await factory.CreateAsync(ada)).Request!.Id;
        int labeling = (await factory.CreateAsync(ada, new TitleKey(MediaKind.Show, 1399))).Request!.Id;
        foreach (int id in new[] { dispatch, labeling })
        {
            await factory.MoveAsync(id, RequestState.Approved, RequestApiFactory.Parent);
            await factory.MoveAsync(id, RequestState.Fulfilling, RequestApiFactory.Dispatch);
        }

        await factory.MoveAsync(dispatch, RequestState.Failed, RequestApiFactory.Dispatch, "Download client unreachable");
        await factory.MoveAsync(labeling, RequestState.Available, RequestApiFactory.Dispatch);
        await factory.MoveAsync(labeling, RequestState.Failed, RequestActor.System("labels"), "Plex did not keep the label change.");

        RequestResult retriedDispatch = await factory.RetryAsync(dispatch, RequestApiFactory.Parent);
        RequestResult retriedLabeling = await factory.RetryAsync(labeling, RequestActor.System("labels"));
        RequestResult repeated = await factory.RetryAsync(dispatch, RequestApiFactory.Parent);

        Assert.Equal(RequestOutcome.Changed, retriedDispatch.Outcome);
        Assert.Equal(RequestState.Fulfilling, retriedDispatch.Request!.State);
        Assert.Equal(RequestOutcome.Changed, retriedLabeling.Outcome);
        Assert.Equal(RequestState.Available, retriedLabeling.Request!.State);
        Assert.Equal(RequestOutcome.Unchanged, repeated.Outcome);
        MediaRequestEvent failure = (await factory.GetHistoryAsync(dispatch))[^2];
        Assert.Equal(RequestState.Failed, failure.ToState);
        Assert.Equal("Download client unreachable", failure.Reason);
        MediaRequestEvent retry = (await factory.GetHistoryAsync(dispatch))[^1];
        Assert.Equal(RequestState.Failed, retry.FromState);
        Assert.Equal(RequestActorKind.Parent, retry.ActorKind);
    }

    [Fact]
    public async Task Only_failed_requests_can_be_retried()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int id = (await factory.CreateAsync(ada)).Request!.Id;
        await factory.MoveAsync(id, RequestState.Approved, RequestApiFactory.Parent);

        RequestResult result = await factory.RetryAsync(id, RequestApiFactory.Parent);

        Assert.Equal(RequestOutcome.Rejected, result.Outcome);
        Assert.Equal(RequestState.Approved, Assert.Single(await factory.GetRequestsAsync()).State);
    }

    [Fact]
    public async Task Competing_decisions_let_exactly_one_win()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int id = (await factory.CreateAsync(ada)).Request!.Id;

        RequestResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(index => Task.Run(() => factory.MoveAsync(
                id,
                index % 2 == 0 ? RequestState.Approved : RequestState.Denied,
                RequestActor.Parent(index, $"parent {index}")))));

        RequestResult winner = Assert.Single(results, result => result.Outcome == RequestOutcome.Changed);
        Assert.All(
            results.Where(result => result != winner),
            result => Assert.True(result.Outcome is RequestOutcome.Unchanged or RequestOutcome.Rejected));
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(winner.Request!.State, request.State);
        Assert.Equal(1, request.Version);
        Assert.Equal(2, (await factory.GetHistoryAsync(id)).Count);
    }

    [Fact]
    public async Task A_move_that_names_the_wrong_starting_state_changes_nothing()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int id = (await factory.CreateAsync(ada)).Request!.Id;
        await factory.MoveAsync(id, RequestState.Approved, RequestApiFactory.Parent);
        await factory.MoveAsync(id, RequestState.Available, RequestApiFactory.Dispatch);
        await factory.MoveAsync(id, RequestState.Failed, RequestApiFactory.Dispatch, "No label");

        RequestResult denied = await factory.WithRequestsAsync(requests => requests.TransitionAsync(
            id,
            RequestState.Denied,
            RequestApiFactory.Parent,
            expectedFrom: RequestState.Pending));
        RequestResult matching = await factory.WithRequestsAsync(requests => requests.TransitionAsync(
            id,
            RequestState.Available,
            RequestApiFactory.Dispatch,
            expectedFrom: RequestState.Failed));

        Assert.Equal(RequestOutcome.Rejected, denied.Outcome);
        Assert.Equal(RequestState.Failed, denied.Request!.State);
        Assert.Equal(RequestOutcome.Changed, matching.Outcome);
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Denied);
    }

    [Fact]
    public async Task Simultaneous_duplicate_requests_end_up_as_one()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");

        RequestResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => factory.CreateAsync(ada))));

        Assert.Single(results, result => result.Outcome == RequestOutcome.Created);
        Assert.Equal(7, results.Count(result => result.Outcome == RequestOutcome.Merged));
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(8, request.RequestCount);
        Assert.Single(await factory.GetHistoryAsync(request.Id));
    }

    [Fact]
    public async Task A_stale_copy_in_the_same_scope_is_refreshed_before_deciding()
    {
        using RequestApiFactory factory = new();
        int ada = await factory.CreateProfileAsync("Ada");
        int id = (await factory.CreateAsync(ada)).Request!.Id;
        using IServiceScope scope = factory.Services.CreateScope();
        MediaRequests requests = scope.ServiceProvider.GetRequiredService<MediaRequests>();
        Assert.Equal(RequestOutcome.Unchanged, (await requests.TransitionAsync(id, RequestState.Pending, RequestApiFactory.Parent)).Outcome);

        await factory.MoveAsync(id, RequestState.Denied, RequestApiFactory.Parent);
        RequestResult result = await requests.TransitionAsync(id, RequestState.Approved, RequestApiFactory.Parent);

        Assert.Equal(RequestOutcome.Rejected, result.Outcome);
        Assert.Equal(RequestState.Denied, result.Request!.State);
    }
}
