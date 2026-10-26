using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Requests;

public sealed class RequestAccessTests
{
    [Fact]
    public async Task Grant_applies_the_label_and_records_the_parent()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        ParentAccount account = await ParentAsync(factory);

        JsonElement body = await GrantAsync(parent, ada, 603);
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        List<MediaRequestEvent> history = await factory.GetHistoryAsync(request.Id);
        OwnedLabel owned = Assert.Single(await factory.GetLedgerAsync());
        MediaRequestEvent created = history[0];

        Assert.Equal("labeled", body.GetProperty("state").GetString());
        Assert.Equal("The Matrix", body.GetProperty("title").GetString());
        Assert.Equal(RequestState.Labeled, request.State);
        Assert.False(request.CountsTowardAllowance);
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
        Assert.Equal(ada, owned.KidProfileId);
        Assert.Equal(RequestAccess.GrantSource, owned.Source);
        Assert.Equal("plex://movie/5d7768ba96b655001fdc0408", owned.Guid);
        Assert.Equal(RequestActorKind.Parent, created.ActorKind);
        Assert.Equal(account.Id, created.ActorId);
        Assert.Equal(account.Username, created.ActorName);
        Assert.Equal(RequestAccess.GrantReason, created.Reason);
        Assert.Equal(
            [RequestState.Pending, RequestState.Approved, RequestState.Available, RequestState.Labeled],
            history.Select(item => item.ToState));
        Assert.Equal(RequestActorKind.Parent, history[1].ActorKind);
        Assert.All(history.Skip(2), item =>
        {
            Assert.Equal(RequestActorKind.System, item.ActorKind);
            Assert.Equal(RequestAccess.GrantComponent, item.ActorName);
            Assert.Null(item.ActorId);
        });
    }

    [Fact]
    public async Task Granting_a_blocked_title_is_recorded_as_a_parent_grant()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int ada) = await factory.SignInKidAsync(movieAgeLimit: 10);
        HttpClient parent = await factory.CreateSignedInClientAsync();

        HttpResponseMessage blocked = await KidRequestApiFactory.RequestAsync(kid, "movie", 603);
        JsonElement body = await GrantAsync(parent, ada, 603);
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        MediaRequestEvent created = (await factory.GetHistoryAsync(request.Id))[0];

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal("labeled", body.GetProperty("state").GetString());
        Assert.Equal(RequestState.Labeled, request.State);
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
        Assert.Equal(RequestActorKind.Parent, created.ActorKind);
        Assert.Equal(RequestAccess.GrantReason, created.Reason);
        Assert.Equal(RequestAccess.GrantSource, Assert.Single(await factory.GetLedgerAsync()).Source);
    }

    [Fact]
    public async Task Grant_does_not_label_a_title_that_is_not_in_the_library_or_unreachable()
    {
        using KidRequestApiFactory missing = new();
        await missing.LinkPlexAsync();
        (_, int ada) = await missing.SignInKidAsync();
        HttpClient parent = await missing.CreateSignedInClientAsync();

        HttpResponseMessage absent = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 700 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, absent.StatusCode);
        Assert.Empty(await missing.GetRequestsAsync());
        Assert.Empty(await missing.GetLedgerAsync());
        Assert.Equal(0, missing.LabelWrites);

        using KidRequestApiFactory offline = new();
        (_, int bea) = await offline.SignInKidAsync("Bea");
        HttpClient offlineParent = await offline.CreateSignedInClientAsync();
        HttpResponseMessage down = await offlineParent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = bea, kind = "movie", tmdbId = 603 });

        Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
        Assert.Empty(await offline.GetRequestsAsync());
        Assert.Empty(await offline.GetLedgerAsync());
    }

    [Fact]
    public async Task A_failed_label_write_leaves_a_failed_grant_that_granting_again_finishes()
    {
        using KidRequestApiFactory factory = new(MatrixIndex());
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        factory.Plex.ServerTokenOverride = "not-the-token";

        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 603 });
        MediaRequest failed = Assert.Single(await factory.GetRequestsAsync());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(RequestState.Failed, failed.State);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Empty(factory.Plex.Item("101").Labels);

        factory.Plex.ServerTokenOverride = null;
        JsonElement again = await GrantAsync(parent, ada, 603);

        Assert.Equal(failed.Id, again.GetProperty("id").GetInt32());
        Assert.Equal("labeled", again.GetProperty("state").GetString());
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
        Assert.Equal(RequestAccess.GrantSource, Assert.Single(await factory.GetLedgerAsync()).Source);
    }

    [Fact]
    public async Task Grant_does_not_label_a_title_another_request_already_holds()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int ada) = await factory.SignInKidAsync(movieAgeLimit: 10);
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int pending = await AskAsync(kid, 129);

        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 129 });
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(pending, request.Id);
        Assert.Equal(RequestState.Pending, request.State);
        Assert.Equal(0, factory.LabelWrites);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Empty(factory.Plex.Item("102").Labels);
    }

    [Fact]
    public async Task Granting_again_returns_the_labeled_request_without_writing()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await GrantAsync(parent, ada, 603)).GetProperty("id").GetInt32();
        int writes = factory.LabelWrites;

        HttpResponseMessage again = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 603 });
        JsonElement body = await again.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(id, body.GetProperty("id").GetInt32());
        Assert.Equal("labeled", body.GetProperty("state").GetString());
        Assert.Equal(writes, factory.LabelWrites);
        Assert.Single(await factory.GetRequestsAsync());
        Assert.Single(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Grant_returns_not_found_for_a_missing_profile_or_title()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        HttpResponseMessage noProfile = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada + 100, kind = "movie", tmdbId = 603 });
        HttpResponseMessage noTitle = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 999999 });

        Assert.Equal(HttpStatusCode.NotFound, noProfile.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noTitle.StatusCode);
        Assert.Empty(await factory.GetRequestsAsync());
        Assert.Equal(0, factory.LabelWrites);
    }

    [Fact]
    public async Task A_hand_added_label_is_reported_on_grant_and_left_on_revoke()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        factory.Plex.Item("101").SetLabels("Ada");

        JsonElement granted = await GrantAsync(parent, ada, 603);
        int id = granted.GetProperty("id").GetInt32();
        JsonElement revoked = await RevokeAsync(parent, new { ids = new[] { id } });

        Assert.True(granted.GetProperty("labelNotOwned").GetBoolean());
        Assert.Equal("succeeded", Item(revoked, id).GetProperty("status").GetString());
        Assert.True(Item(revoked, id).GetProperty("labelNotOwned").GetBoolean());
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Preview_names_the_title_and_says_whether_it_is_in_the_library()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        JsonElement inLibrary = await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests/grant/preview?kind=movie&tmdbId=603");
        JsonElement missing = await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests/grant/preview?kind=movie&tmdbId=700");
        HttpResponseMessage unknown = await parent.GetAsync("/api/v1/requests/grant/preview?kind=movie&tmdbId=999999");
        HttpResponseMessage invalid = await parent.GetAsync("/api/v1/requests/grant/preview?kind=book&tmdbId=603");

        Assert.Equal("The Matrix", inLibrary.GetProperty("title").GetString());
        Assert.True(inLibrary.GetProperty("inLibrary").GetBoolean());
        Assert.Equal("Movie 700", missing.GetProperty("title").GetString());
        Assert.False(missing.GetProperty("inLibrary").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(await factory.GetRequestsAsync());
        Assert.Equal(0, factory.LabelWrites);
    }

    [Fact]
    public async Task Revoke_removes_only_the_label_this_app_owns_and_records_the_parent()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        (_, int ben) = await factory.SignInKidAsync("Ben");
        HttpClient parent = await factory.CreateSignedInClientAsync();
        ParentAccount account = await ParentAsync(factory);
        int adaRequest = (await GrantAsync(parent, ada, 603)).GetProperty("id").GetInt32();
        int benRequest = (await GrantAsync(parent, ben, 603)).GetProperty("id").GetInt32();
        factory.Plex.Item("101").AddLabel("Favorites");

        JsonElement body = await RevokeAsync(parent, new { ids = new[] { adaRequest }, reason = "  Too old  " });
        MediaRequest revoked = (await factory.GetRequestsAsync()).Single(request => request.Id == adaRequest);
        MediaRequest kept = (await factory.GetRequestsAsync()).Single(request => request.Id == benRequest);
        MediaRequestEvent removal = (await factory.GetHistoryAsync(adaRequest)).Single(item => item.ToState == RequestState.Revoked);
        OwnedLabel owned = Assert.Single(await factory.GetLedgerAsync());

        Assert.Equal("succeeded", Item(body, adaRequest).GetProperty("status").GetString());
        Assert.Equal("revoked", Item(body, adaRequest).GetProperty("state").GetString());
        Assert.False(Item(body, adaRequest).GetProperty("labelNotOwned").GetBoolean());
        Assert.Equal(RequestState.Revoked, revoked.State);
        Assert.Equal(RequestState.Labeled, kept.State);
        Assert.Equal(["Ben", "Favorites"], factory.Plex.Item("101").Labels);
        Assert.Equal(ben, owned.KidProfileId);
        Assert.Equal(RequestActorKind.Parent, removal.ActorKind);
        Assert.Equal(account.Id, removal.ActorId);
        Assert.Equal(account.Username, removal.ActorName);
        Assert.Equal("Too old", removal.Reason);
    }

    [Fact]
    public async Task Revoke_leaves_a_label_the_app_did_not_apply()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        factory.Plex.Item("101").SetLabels("Ada", "Favorites");
        int id = await AskAsync(kid, 603);
        int writes = factory.LabelWrites;

        JsonElement body = await RevokeAsync(parent, new { ids = new[] { id } });

        Assert.Equal("succeeded", Item(body, id).GetProperty("status").GetString());
        Assert.True(Item(body, id).GetProperty("labelNotOwned").GetBoolean());
        Assert.Equal(RequestState.Revoked, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada", "Favorites"], factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Equal(writes, factory.LabelWrites);
    }

    [Fact]
    public async Task A_label_removal_that_Plex_refuses_does_not_mark_the_request_revoked()
    {
        using KidRequestApiFactory factory = new(MatrixIndex());
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await GrantAsync(parent, ada, 603)).GetProperty("id").GetInt32();
        factory.Plex.Delay = request => request.Method == "PUT" ? throw new InvalidOperationException("refused") : Task.CompletedTask;

        JsonElement body = await RevokeAsync(parent, new { ids = new[] { id } });

        AssertNotRevoked(body, id, await factory.GetRequestsAsync(), await factory.GetHistoryAsync(id));
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
        Assert.Single(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task A_label_removal_that_finds_the_label_no_longer_owned_does_not_mark_the_request_revoked()
    {
        using KidRequestApiFactory factory = new(MatrixIndex());
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await GrantAsync(parent, ada, 603)).GetProperty("id").GetInt32();
        int metadataReads = 0;
        factory.Plex.Delay = async request =>
        {
            // The first read finds the ledger row; the second starts the removal, which then finds the row gone.
            if (request.Method == "GET" && request.Path == "/library/metadata/101" && Interlocked.Increment(ref metadataReads) == 2)
            {
                await ClearLedgerAsync(factory);
            }
        };
        int writes = factory.LabelWrites;

        JsonElement body = await RevokeAsync(parent, new { ids = new[] { id } });

        AssertNotRevoked(body, id, await factory.GetRequestsAsync(), await factory.GetHistoryAsync(id));
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
        Assert.Equal(writes, factory.LabelWrites);
    }

    [Fact]
    public async Task Revoking_an_available_request_is_a_conflict_while_its_label_is_being_written()
    {
        using KidRequestApiFactory factory = new(SpiritedAwayIndex());
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = await AskAsync(kid, 129);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Plex.Delay = async request =>
        {
            if (request.Method == "PUT")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };

        Task<HttpResponseMessage> approve = parent.PostAsJsonAsync("/api/v1/requests/approve", new { ids = new[] { id } });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int writes = factory.LabelWrites;
        JsonElement early = await RevokeAsync(parent, new { ids = new[] { id } });
        int writesDuringRevoke = factory.LabelWrites - writes;
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await approve).StatusCode);
        factory.Plex.Delay = null;

        Assert.Equal("conflict", Item(early, id).GetProperty("status").GetString());
        Assert.Equal("available", Item(early, id).GetProperty("state").GetString());
        Assert.Equal(0, writesDuringRevoke);
        Assert.Equal(RequestState.Labeled, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada"], factory.Plex.Item("102").Labels);
        Assert.Single(await factory.GetLedgerAsync());

        JsonElement later = await RevokeAsync(parent, new { ids = new[] { id } });

        Assert.Equal("succeeded", Item(later, id).GetProperty("status").GetString());
        Assert.Equal(RequestState.Revoked, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Empty(factory.Plex.Item("102").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Two_revokes_at_once_remove_the_label_and_revoke_once()
    {
        FakeLibraryIndex index = MatrixIndex();
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient first = await factory.CreateSignedInClientAsync();
        HttpClient second = await factory.CreateSignedInClientAsync();
        int id = (await GrantAsync(first, ada, 603)).GetProperty("id").GetInt32();
        Rendezvous both = new(2);
        index.Gate = both.ArriveAsync;

        JsonElement[] results = await Task.WhenAll(
            RevokeAsync(first, new { ids = new[] { id } }),
            RevokeAsync(second, new { ids = new[] { id } }));

        Assert.Equal(
            ["alreadyDecided", "succeeded"],
            results.Select(body => Item(body, id).GetProperty("status").GetString()).Order());
        Assert.Equal(RequestState.Revoked, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Revoked);
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Revoke_matches_the_ledger_by_server_guid_and_label()
    {
        using KidRequestApiFactory moved = new();
        await moved.LinkPlexAsync();
        (_, int ada) = await moved.SignInKidAsync();
        HttpClient parent = await moved.CreateSignedInClientAsync();
        int id = (await GrantAsync(parent, ada, 603)).GetProperty("id").GetInt32();
        await UpdateLedgerAsync(moved, row => row.RatingKey = "999");

        JsonElement removed = await RevokeAsync(parent, new { ids = new[] { id } });

        Assert.Equal("succeeded", Item(removed, id).GetProperty("status").GetString());
        Assert.False(Item(removed, id).GetProperty("labelNotOwned").GetBoolean());
        Assert.Empty(moved.Plex.Item("101").Labels);
        Assert.Empty(await moved.GetLedgerAsync());

        using KidRequestApiFactory other = new();
        await other.LinkPlexAsync();
        (_, int bea) = await other.SignInKidAsync("Bea");
        HttpClient otherParent = await other.CreateSignedInClientAsync();
        int otherId = (await GrantAsync(otherParent, bea, 603)).GetProperty("id").GetInt32();
        await UpdateLedgerAsync(other, row => row.Guid = "plex://movie/another-title");

        JsonElement kept = await RevokeAsync(otherParent, new { ids = new[] { otherId } });

        Assert.Equal("succeeded", Item(kept, otherId).GetProperty("status").GetString());
        Assert.True(Item(kept, otherId).GetProperty("labelNotOwned").GetBoolean());
        Assert.Equal(["Bea"], other.Plex.Item("101").Labels);
        Assert.Single(await other.GetLedgerAsync());
    }

    [Fact]
    public async Task Revoking_again_does_not_remove_twice_and_a_pending_request_is_a_conflict()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await GrantAsync(parent, ada, 603)).GetProperty("id").GetInt32();
        int pending = await AskAsync(kid, 700);
        await RevokeAsync(parent, new { ids = new[] { id } });
        int writes = factory.LabelWrites;

        JsonElement again = await RevokeAsync(parent, new { ids = new[] { id, pending } });

        Assert.Equal("alreadyDecided", Item(again, id).GetProperty("status").GetString());
        Assert.Equal("revoked", Item(again, id).GetProperty("state").GetString());
        Assert.Equal("conflict", Item(again, pending).GetProperty("status").GetString());
        Assert.Equal("pending", Item(again, pending).GetProperty("state").GetString());
        Assert.Equal(writes, factory.LabelWrites);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Revoked);
        Assert.Equal(RequestState.Pending, (await factory.GetRequestsAsync()).Single(request => request.Id == pending).State);
    }

    [Fact]
    public async Task A_request_for_an_archived_profile_can_still_be_revoked()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await GrantAsync(parent, ada, 603)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await parent.DeleteAsync($"/api/v1/kid-profiles/{ada}")).StatusCode);

        JsonElement body = await RevokeAsync(parent, new { ids = new[] { id } });

        Assert.Equal("succeeded", Item(body, id).GetProperty("status").GetString());
        Assert.Equal(RequestState.Revoked, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task A_revoked_title_can_be_requested_again_only_with_parent_approval()
    {
        using KidRequestApiFactory instant = new();
        await instant.LinkPlexAsync();
        (HttpClient kid, _) = await instant.SignInKidAsync();
        HttpClient parent = await instant.CreateSignedInClientAsync();
        int labeled = await AskAsync(kid, 603);
        await RevokeAsync(parent, new { ids = new[] { labeled } });
        Assert.Empty(instant.Plex.Item("101").Labels);

        HttpResponseMessage again = await KidRequestApiFactory.RequestAsync(kid, "movie", 603);
        JsonElement body = await again.Content.ReadFromJsonAsync<JsonElement>();
        List<MediaRequest> requests = await instant.GetRequestsAsync();

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal("pending", body.GetProperty("state").GetString());
        Assert.Equal(RequestState.Pending, requests.Single(request => request.Id != labeled).State);
        Assert.True(requests.Single(request => request.Id != labeled).CountsTowardAllowance);
        Assert.Empty(instant.Plex.Item("101").Labels);
        Assert.Empty(await instant.GetLedgerAsync());

        using KidRequestApiFactory blocked = new();
        await blocked.LinkPlexAsync();
        (HttpClient limited, int limitedId) = await blocked.SignInKidAsync("Cy", movieAgeLimit: 10);
        HttpClient limitedParent = await blocked.CreateSignedInClientAsync();
        int granted = (await GrantAsync(limitedParent, limitedId, 603)).GetProperty("id").GetInt32();
        await RevokeAsync(limitedParent, new { ids = new[] { granted } });

        HttpResponseMessage queued = await KidRequestApiFactory.RequestAsync(limited, "movie", 603);

        Assert.Equal(HttpStatusCode.Created, queued.StatusCode);
        Assert.Equal("pending", (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Empty(blocked.Plex.Item("101").Labels);
    }

    [Fact]
    public async Task A_revoked_title_is_not_auto_approved_when_asked_for_again()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 700, new MediaItem("101", "plex://movie/5d7768ba96b655001fdc0408", "Movie 700", 1999, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync("Dee", autoApprove: true);
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int labeled = await AskAsync(kid, 700);
        Assert.Equal(RequestState.Labeled, Assert.Single(await factory.GetRequestsAsync()).State);
        await RevokeAsync(parent, new { ids = new[] { labeled } });
        index.Remove(MediaKind.Movie, 700);

        HttpResponseMessage again = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);
        MediaRequest created = (await factory.GetRequestsAsync()).Single(request => request.Id != labeled);

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal("pending", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(RequestState.Pending, created.State);
        Assert.DoesNotContain(await factory.GetHistoryAsync(created.Id), item => item.ToState == RequestState.Approved);
        Assert.Empty(factory.Plex.Item("101").Labels);
    }

    [Fact]
    public async Task The_same_title_not_in_the_library_is_auto_approved_without_a_revoke()
    {
        using KidRequestApiFactory factory = new(new FakeLibraryIndex());
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync("Dee", autoApprove: true);

        HttpResponseMessage asked = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);

        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        Assert.Equal("approved", (await asked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_revoke_that_lands_while_the_kid_asks_again_keeps_the_new_request_waiting()
    {
        FakeLibraryIndex index = MatrixIndex();
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int labeled = await AskAsync(kid, 603);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int lookups = 0;
        index.Gate = async () =>
        {
            if (Interlocked.Increment(ref lookups) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };

        Task<HttpResponseMessage> asking = KidRequestApiFactory.RequestAsync(kid, "movie", 603);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        JsonElement revoked = await RevokeAsync(parent, new { ids = new[] { labeled } });
        release.SetResult();
        HttpResponseMessage again = await asking;
        MediaRequest created = (await factory.GetRequestsAsync()).Single(request => request.Id != labeled);

        Assert.Equal("succeeded", Item(revoked, labeled).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal("pending", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(RequestState.Pending, created.State);
        Assert.True(created.CountsTowardAllowance);
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Kid_history_words_automatic_and_parent_approvals_differently()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        (HttpClient other, _) = await factory.SignInKidAsync("Ben", movieAgeLimit: 10);
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int pending = await AskAsync(kid, 129);
        Assert.Equal(HttpStatusCode.OK, (await parent.PostAsJsonAsync("/api/v1/requests/approve", new { ids = new[] { pending } })).StatusCode);
        HttpResponseMessage asked = await KidRequestApiFactory.RequestAsync(other, "show", 82728);
        JsonElement instantBody = await asked.Content.ReadFromJsonAsync<JsonElement>();
        int instant = instantBody.GetProperty("id").GetInt32();
        Assert.Equal("labeled", instantBody.GetProperty("state").GetString());

        JsonElement[] parentApproved = await kid.GetFromJsonAsync<JsonElement[]>("/api/v1/kid/requests/history") ?? [];
        JsonElement[] systemApproved = await other.GetFromJsonAsync<JsonElement[]>("/api/v1/kid/requests/history") ?? [];

        Assert.Contains(parentApproved, entry => entry.GetProperty("action").GetString() == "approved" && entry.GetProperty("requestId").GetInt32() == pending);
        Assert.DoesNotContain(parentApproved, entry => entry.GetProperty("action").GetString() == "autoApproved");
        Assert.Contains(systemApproved, entry => entry.GetProperty("action").GetString() == "autoApproved" && entry.GetProperty("requestId").GetInt32() == instant);
        Assert.DoesNotContain(systemApproved, entry => entry.GetProperty("action").GetString() == "approved");
    }

    [Fact]
    public async Task Kid_history_shows_the_denial_note_only_to_that_kid_and_is_paged()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient ada, _) = await factory.SignInKidAsync();
        (HttpClient ben, _) = await factory.SignInKidAsync("Ben");
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = await AskAsync(ada, 700);
        await AskAsync(ben, 701);
        Assert.Equal(HttpStatusCode.OK, (await parent.PostAsJsonAsync("/api/v1/requests/deny", new { ids = new[] { id }, reason = "  Not on a school night  " })).StatusCode);

        JsonElement[] mine = await ada.GetFromJsonAsync<JsonElement[]>("/api/v1/kid/requests/history") ?? [];
        string theirs = await ben.GetStringAsync("/api/v1/kid/requests/history");
        JsonElement[] newest = await ada.GetFromJsonAsync<JsonElement[]>("/api/v1/kid/requests/history?limit=1") ?? [];
        JsonElement[] older = await ada.GetFromJsonAsync<JsonElement[]>("/api/v1/kid/requests/history?limit=1&offset=1") ?? [];
        HttpResponseMessage tooMany = await ada.GetAsync("/api/v1/kid/requests/history?limit=500");

        JsonElement denied = Assert.Single(mine, entry => entry.GetProperty("action").GetString() == "denied");
        Assert.Equal("Not on a school night", denied.GetProperty("note").GetString());
        Assert.DoesNotContain("school night", theirs, StringComparison.Ordinal);
        Assert.Equal("denied", Assert.Single(newest).GetProperty("action").GetString());
        Assert.Equal("asked", Assert.Single(older).GetProperty("action").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task Kid_history_shows_grants_and_revocations_without_the_parent_account()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient ada, int adaId) = await factory.SignInKidAsync();
        (HttpClient ben, _) = await factory.SignInKidAsync("Ben");
        HttpClient parent = await factory.CreateSignedInClientAsync();
        ParentAccount account = await ParentAsync(factory);
        int id = (await GrantAsync(parent, adaId, 603)).GetProperty("id").GetInt32();
        await RevokeAsync(parent, new { ids = new[] { id }, reason = account.Username });

        HttpResponseMessage history = await ada.GetAsync("/api/v1/kid/requests/history");
        string json = await history.Content.ReadAsStringAsync();
        JsonElement entries = JsonSerializer.Deserialize<JsonElement>(json);
        HttpResponseMessage benHistory = await ben.GetAsync("/api/v1/kid/requests/history");
        HttpResponseMessage asParent = await parent.GetAsync("/api/v1/kid/requests/history");

        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal(HttpStatusCode.OK, benHistory.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asParent.StatusCode);
        Assert.Equal([], await benHistory.Content.ReadFromJsonAsync<JsonElement[]>());
        Assert.Contains(entries.EnumerateArray(), entry => entry.GetProperty("action").GetString() == "granted" && entry.GetProperty("title").GetString() == "The Matrix");
        Assert.Contains(entries.EnumerateArray(), entry => entry.GetProperty("action").GetString() == "revoked" && entry.GetProperty("requestId").GetInt32() == id);
        Assert.All(entries.EnumerateArray(), entry => Assert.Equal(JsonValueKind.Null, entry.GetProperty("note").ValueKind));
        Assert.DoesNotContain(account.Username, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actorName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actorId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_kid_cannot_grant_or_revoke_and_a_parent_cookie_needs_the_antiforgery_token()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int ada) = await factory.SignInKidAsync();
        int id = await AskAsync(kid, 603);
        HttpClient parent = await factory.CreateSignedInClientAsync();
        HttpClient unsigned = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await unsigned.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword })).StatusCode);

        HttpResponseMessage kidGrant = await kid.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 129 });
        HttpResponseMessage kidRevoke = await kid.PostAsJsonAsync("/api/v1/requests/revoke", new { ids = new[] { id } });
        HttpResponseMessage noTokenGrant = await unsigned.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 129 });
        HttpResponseMessage noTokenRevoke = await unsigned.PostAsJsonAsync("/api/v1/requests/revoke", new { ids = new[] { id } });
        JsonElement revoked = await RevokeAsync(parent, new { ids = new[] { id } });

        Assert.Equal(HttpStatusCode.Forbidden, kidGrant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, kidRevoke.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noTokenGrant.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noTokenRevoke.StatusCode);
        Assert.Equal("succeeded", Item(revoked, id).GetProperty("status").GetString());
        Assert.Equal(RequestState.Revoked, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Empty(factory.Plex.Item("101").Labels);
    }

    [Fact]
    public async Task Two_revokes_with_a_failing_second_copy_end_labeled_or_revoked_and_never_in_between()
    {
        FakeLibraryIndex index = TwoCopyIndex();
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient first = await factory.CreateSignedInClientAsync();
        HttpClient second = await factory.CreateSignedInClientAsync();
        int id = (await GrantAsync(first, ada, 603)).GetProperty("id").GetInt32();
        Assert.Equal(2, (await factory.GetLedgerAsync()).Count);
        int refused = 0;
        factory.Plex.Delay = request =>
            request.Method == "PUT" && request.Query.GetValueOrDefault("id") == "102" && Interlocked.Increment(ref refused) == 1
                ? throw new InvalidOperationException("refused")
                : Task.CompletedTask;
        Rendezvous both = new(2);
        index.Gate = both.ArriveAsync;

        JsonElement[] results = await Task.WhenAll(
            RevokeAsync(first, new { ids = new[] { id } }),
            RevokeAsync(second, new { ids = new[] { id } }));
        index.Gate = null;
        factory.Plex.Delay = null;
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        List<OwnedLabel> ledger = await factory.GetLedgerAsync();

        if (request.State == RequestState.Labeled)
        {
            Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
            Assert.Equal(["Ada"], factory.Plex.Item("102").Labels);
            Assert.Equal(2, ledger.Count);
        }
        else
        {
            Assert.Equal(RequestState.Revoked, request.State);
            Assert.Empty(factory.Plex.Item("101").Labels);
            Assert.Empty(factory.Plex.Item("102").Labels);
            Assert.Empty(ledger);
        }

        Assert.Equal(
            ["failed", "succeeded"],
            results.Select(body => Item(body, id).GetProperty("status").GetString()).Order());
        Assert.Equal(RequestState.Revoked, request.State);
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Revoked);
    }

    [Fact]
    public async Task A_second_grant_while_the_first_is_labeling_does_not_label_after_a_revoke()
    {
        using KidRequestApiFactory factory = new(MatrixIndex());
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient first = await factory.CreateSignedInClientAsync();
        HttpClient second = await factory.CreateSignedInClientAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int puts = 0;
        factory.Plex.Delay = async request =>
        {
            if (request.Method == "PUT" && Interlocked.Increment(ref puts) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };

        Task<HttpResponseMessage> granting = first.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 603 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<HttpResponseMessage> again = second.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 603 });
        release.SetResult();
        HttpResponseMessage granted = await granting;
        HttpResponseMessage repeated = await again;
        int id = (await granted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        JsonElement revoked = await RevokeAsync(first, new { ids = new[] { id } });
        int writes = factory.LabelWrites;
        factory.Plex.Delay = null;

        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal("labeled", (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal("succeeded", Item(revoked, id).GetProperty("status").GetString());
        Assert.Equal(RequestState.Revoked, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Single(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Labeled);
        Assert.Equal(writes, factory.LabelWrites);
    }

    [Fact]
    public async Task A_grant_does_not_label_a_request_it_did_not_move_to_available()
    {
        using KidRequestApiFactory factory = new(MatrixIndex());
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        factory.Plex.ServerTokenOverride = "not-the-token";
        HttpResponseMessage failed = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 603 });
        factory.Plex.ServerTokenOverride = null;
        int id = Assert.Single(await factory.GetRequestsAsync()).Id;
        await SetAvailableAsync(factory, id);
        int writes = factory.LabelWrites;

        HttpResponseMessage again = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 603 });

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(writes, factory.LabelWrites);
        Assert.Equal(RequestState.Available, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task A_failed_second_copy_takes_the_first_copy_label_off_again()
    {
        using KidRequestApiFactory factory = new(TwoCopyIndex());
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        factory.Plex.Delay = request =>
            request.Method == "PUT" && request.Query.GetValueOrDefault("id") == "102"
                ? throw new InvalidOperationException("refused")
                : Task.CompletedTask;

        int id = await AskAsync(kid, 603);
        factory.Plex.Delay = null;
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());

        Assert.Equal(id, request.Id);
        Assert.Equal(RequestState.Failed, request.State);
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Empty(factory.Plex.Item("102").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task A_failed_request_that_kept_a_label_is_offered_for_revoke_and_revoking_removes_it()
    {
        using KidRequestApiFactory factory = new(TwoCopyIndex());
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = await AskWithStrandedLabelAsync(factory, kid);

        JsonElement listed = await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests?state=failed");
        JsonElement revoked = await RevokeAsync(parent, new { ids = new[] { id } });

        Assert.True(Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("canRevoke").GetBoolean());
        Assert.Equal("succeeded", Item(revoked, id).GetProperty("status").GetString());
        Assert.Equal(RequestState.Revoked, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task A_timed_out_write_that_never_reached_Plex_is_offered_for_revoke_and_revoking_clears_its_row()
    {
        using DeadlineFactory factory = new(MatrixIndex(), TimeSpan.FromMilliseconds(300));
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        TaskCompletionSource hang = new(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Plex.Delay = request => request.Method == "PUT" ? hang.Task : Task.CompletedTask;

        try
        {
            int id = await AskAsync(kid, 603);
            Assert.Equal(RequestState.Failed, Assert.Single(await factory.GetRequestsAsync()).State);
            Assert.Single(await factory.GetLedgerAsync());
            Assert.Empty(factory.Plex.Item("101").Labels);
            int writes = factory.LabelWrites;

            JsonElement listed = await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests?state=failed");
            JsonElement revoked = await RevokeAsync(parent, new { ids = new[] { id } });

            Assert.True(Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("canRevoke").GetBoolean());
            Assert.Equal("succeeded", Item(revoked, id).GetProperty("status").GetString());
            Assert.False(Item(revoked, id).GetProperty("labelNotOwned").GetBoolean());
            Assert.Equal(RequestState.Revoked, Assert.Single(await factory.GetRequestsAsync()).State);
            Assert.Empty(await factory.GetLedgerAsync());
            Assert.Equal(writes, factory.LabelWrites);
        }
        finally
        {
            hang.SetException(new InvalidOperationException("dropped"));
        }
    }

    [Fact]
    public async Task The_parent_list_checks_every_failed_request_with_one_index_call_and_no_Plex_call()
    {
        FakeLibraryIndex index = TwoCopyIndex();
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (HttpClient ada, _) = await factory.SignInKidAsync();
        (HttpClient ben, _) = await factory.SignInKidAsync("Ben");
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int stranded = await AskWithStrandedLabelAsync(factory, ada);
        factory.Plex.Delay = request => request.Method == "PUT" ? throw new InvalidOperationException("refused") : Task.CompletedTask;
        int clean = await AskAsync(ben, 603);
        factory.Plex.Delay = null;
        int lookups = index.Calls;
        int plexCalls = factory.Plex.Requests.Count;

        JsonElement listed = await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests?state=failed");
        JsonElement[] items = [.. listed.GetProperty("items").EnumerateArray()];

        Assert.Equal(2, items.Length);
        Assert.True(items.Single(item => item.GetProperty("id").GetInt32() == stranded).GetProperty("canRevoke").GetBoolean());
        Assert.False(items.Single(item => item.GetProperty("id").GetInt32() == clean).GetProperty("canRevoke").GetBoolean());
        Assert.Equal(1, index.GuidCalls);
        Assert.Equal(lookups, index.Calls);
        Assert.Equal(plexCalls, factory.Plex.Requests.Count);
    }

    [Fact]
    public async Task A_failed_request_is_not_offered_for_revoke_before_the_index_can_answer()
    {
        FakeLibraryIndex index = TwoCopyIndex();
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = await AskWithStrandedLabelAsync(factory, kid);
        index.Ready = false;
        int plexCalls = factory.Plex.Requests.Count;

        JsonElement listed = await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests?state=failed");

        JsonElement item = Assert.Single(listed.GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetInt32());
        Assert.False(item.GetProperty("canRevoke").GetBoolean());
        Assert.Equal(plexCalls, factory.Plex.Requests.Count);
    }

    [Fact]
    public async Task A_failed_request_without_labels_is_not_offered_for_revoke()
    {
        using KidRequestApiFactory factory = new(TwoCopyIndex());
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        factory.Plex.Delay = request =>
            request.Method == "PUT" && request.Query.GetValueOrDefault("id") == "102"
                ? throw new InvalidOperationException("refused")
                : Task.CompletedTask;
        await AskAsync(kid, 603);
        factory.Plex.Delay = null;

        JsonElement listed = await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests?state=failed");

        Assert.False(Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("canRevoke").GetBoolean());
    }

    [Fact]
    public async Task A_request_stuck_as_available_can_be_revoked_only_after_the_threshold()
    {
        using KidRequestApiFactory factory = new(TwoCopyIndex());
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = await AskWithStrandedLabelAsync(factory, kid);
        await SetAvailableAsync(factory, id);
        factory.Clock.Advance(TimeSpan.FromMinutes(9));

        JsonElement early = await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests?state=available");
        JsonElement refused = await RevokeAsync(parent, new { ids = new[] { id } });

        Assert.False(Assert.Single(early.GetProperty("items").EnumerateArray()).GetProperty("canRevoke").GetBoolean());
        Assert.Equal("conflict", Item(refused, id).GetProperty("status").GetString());
        Assert.Equal("available", Item(refused, id).GetProperty("state").GetString());
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);

        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        JsonElement late = await parent.GetFromJsonAsync<JsonElement>("/api/v1/requests?state=available");
        JsonElement revoked = await RevokeAsync(parent, new { ids = new[] { id } });

        Assert.True(Assert.Single(late.GetProperty("items").EnumerateArray()).GetProperty("canRevoke").GetBoolean());
        Assert.Equal("succeeded", Item(revoked, id).GetProperty("status").GetString());
        Assert.Equal(RequestState.Revoked, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task A_grant_that_meets_a_new_kid_request_leaves_it_untouched()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int ada) = await factory.SignInKidAsync(movieAgeLimit: 10);
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int asked = 0;
        factory.Saves.GrantCreateGate = async () =>
        {
            if (Interlocked.Increment(ref asked) == 1)
            {
                await AskAsync(kid, 129);
            }
        };

        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 129 });
        factory.Saves.GrantCreateGate = null;
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        List<MediaRequestEvent> history = await factory.GetHistoryAsync(request.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, request.RequestCount);
        Assert.Equal(RequestState.Pending, request.State);
        Assert.True(request.CountsTowardAllowance);
        Assert.Equal("Spirited Away", request.Title);
        Assert.Equal(RequestActorKind.Kid, Assert.Single(history).ActorKind);
        Assert.Equal(0, factory.LabelWrites);
    }

    [Fact]
    public async Task Revoking_an_archived_profile_leaves_a_label_a_newer_profile_uses()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (_, int oldAda) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int oldRequest = (await GrantAsync(parent, oldAda, 603)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await parent.DeleteAsync($"/api/v1/kid-profiles/{oldAda}")).StatusCode);
        (_, int newAda) = await factory.SignInKidAsync();
        int newRequest = (await GrantAsync(parent, newAda, 603)).GetProperty("id").GetInt32();
        int writes = factory.LabelWrites;

        JsonElement body = await RevokeAsync(parent, new { ids = new[] { oldRequest } });
        List<MediaRequest> requests = await factory.GetRequestsAsync();

        Assert.Equal("succeeded", Item(body, oldRequest).GetProperty("status").GetString());
        Assert.True(Item(body, oldRequest).GetProperty("labelInUse").GetBoolean());
        Assert.False(Item(body, oldRequest).GetProperty("labelNotOwned").GetBoolean());
        Assert.Equal(RequestState.Revoked, requests.Single(request => request.Id == oldRequest).State);
        Assert.Equal(RequestState.Labeled, requests.Single(request => request.Id == newRequest).State);
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
        Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal(writes, factory.LabelWrites);
    }

    // The second copy refuses the label and the first refuses to have it taken off again, so the failed request keeps one ledger-owned label.
    private static async Task<int> AskWithStrandedLabelAsync(KidRequestApiFactory factory, HttpClient kid)
    {
        int firstCopyPuts = 0;
        factory.Plex.Delay = request =>
            request.Method == "PUT"
                && (request.Query.GetValueOrDefault("id") == "102"
                    || (request.Query.GetValueOrDefault("id") == "101" && Interlocked.Increment(ref firstCopyPuts) > 1))
                ? throw new InvalidOperationException("refused")
                : Task.CompletedTask;
        int id = await AskAsync(kid, 603);
        factory.Plex.Delay = null;
        Assert.Equal(RequestState.Failed, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
        Assert.Single(await factory.GetLedgerAsync());
        return id;
    }

    private static async Task<int> AskAsync(HttpClient kid, int tmdbId)
    {
        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", tmdbId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> GrantAsync(HttpClient parent, int profileId, int tmdbId)
    {
        HttpResponseMessage response = await parent.PostAsJsonAsync(
            "/api/v1/requests/grant",
            new { kidProfileId = profileId, kind = "movie", tmdbId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static async Task<JsonElement> RevokeAsync(HttpClient parent, object body)
    {
        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/requests/revoke", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static FakeLibraryIndex MatrixIndex()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 603, new MediaItem("101", "plex://movie/5d7768ba96b655001fdc0408", "The Matrix", 1999, MediaKind.Movie, "1", []));
        return index;
    }

    private static FakeLibraryIndex TwoCopyIndex()
    {
        FakeLibraryIndex index = MatrixIndex();
        index.Add(MediaKind.Movie, 603, new MediaItem("102", "plex://movie/5d776b59ad5437001f79c6f8", "The Matrix", 1999, MediaKind.Movie, "1", []));
        return index;
    }

    private static FakeLibraryIndex SpiritedAwayIndex()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 129, new MediaItem("102", "plex://movie/5d776b59ad5437001f79c6f8", "Spirited Away", 2001, MediaKind.Movie, "1", []));
        return index;
    }

    private sealed class DeadlineFactory(FakeLibraryIndex index, TimeSpan labelTimeout) : KidRequestApiFactory(index)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.Configure<ApprovalOptions>(options => options.LabelTimeout = labelTimeout));
        }
    }

    private static void AssertNotRevoked(JsonElement body, int id, List<MediaRequest> requests, List<MediaRequestEvent> history)
    {
        Assert.Equal("failed", Item(body, id).GetProperty("status").GetString());
        Assert.Equal("labeled", Item(body, id).GetProperty("state").GetString());
        Assert.Equal(RequestState.Labeled, Assert.Single(requests).State);
        Assert.DoesNotContain(history, item => item.ToState == RequestState.Revoked);
    }

    private static async Task ClearLedgerAsync(KidRequestApiFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().OwnedLabels.ExecuteDeleteAsync();
    }

    private static async Task UpdateLedgerAsync(KidRequestApiFactory factory, Action<OwnedLabel> change)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        change(await db.OwnedLabels.SingleAsync());
        await db.SaveChangesAsync();
    }

    // Stands for a process that stopped after moving a failed request back to available and before labeling it.
    private static async Task SetAvailableAsync(KidRequestApiFactory factory, int requestId)
    {
        DateTimeOffset now = factory.Clock.GetUtcNow();
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().MediaRequests
            .Where(request => request.Id == requestId && request.State == RequestState.Failed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(request => request.State, RequestState.Available)
                .SetProperty(request => request.StateChangedAt, now)
                .SetProperty(request => request.Version, request => request.Version + 1));
    }

    private static JsonElement Item(JsonElement body, int id) =>
        body.GetProperty("results").EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == id);

    private static async Task<ParentAccount> ParentAsync(KidRequestApiFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().ParentAccounts.AsNoTracking().SingleAsync();
    }
}
