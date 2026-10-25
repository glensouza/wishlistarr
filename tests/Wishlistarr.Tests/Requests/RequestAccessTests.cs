using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task A_failed_label_write_does_not_grant_the_title()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 603, new MediaItem("101", "plex://movie/5d7768ba96b655001fdc0408", "The Matrix", 1999, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        factory.Plex.ServerTokenOverride = "not-the-token";

        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/requests/grant", new { kidProfileId = ada, kind = "movie", tmdbId = 603 });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await factory.GetRequestsAsync());
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Empty(factory.Plex.Item("101").Labels);
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
        Assert.Equal(RequestState.Revoked, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada", "Favorites"], factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Equal(writes, factory.LabelWrites);
    }

    [Fact]
    public async Task A_failed_label_removal_does_not_mark_the_request_revoked()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (_, int ada) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await GrantAsync(parent, ada, 603)).GetProperty("id").GetInt32();
        factory.Plex.ServerTokenOverride = "not-the-token";
        int writes = factory.LabelWrites;

        JsonElement body = await RevokeAsync(parent, new { ids = new[] { id } });

        Assert.Equal("failed", Item(body, id).GetProperty("status").GetString());
        Assert.Equal("labeled", Item(body, id).GetProperty("state").GetString());
        Assert.Equal(RequestState.Labeled, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
        Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal(writes, factory.LabelWrites);
        Assert.DoesNotContain(await factory.GetHistoryAsync(id), item => item.ToState == RequestState.Revoked);
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

        using KidRequestApiFactory automatic = new();
        await automatic.LinkPlexAsync();
        (HttpClient autoKid, _) = await automatic.SignInKidAsync("Dee", autoApprove: true);
        HttpClient autoParent = await automatic.CreateSignedInClientAsync();
        int autoLabeled = await AskAsync(autoKid, 603);
        Assert.Equal(RequestState.Labeled, (await automatic.GetRequestsAsync()).Single().State);
        await RevokeAsync(autoParent, new { ids = new[] { autoLabeled } });

        HttpResponseMessage autoAgain = await KidRequestApiFactory.RequestAsync(autoKid, "movie", 603);

        Assert.Equal(HttpStatusCode.Created, autoAgain.StatusCode);
        Assert.Equal("pending", (await autoAgain.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Empty(automatic.Plex.Item("101").Labels);
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

    private static JsonElement Item(JsonElement body, int id) =>
        body.GetProperty("results").EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == id);

    private static async Task<ParentAccount> ParentAsync(KidRequestApiFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().ParentAccounts.AsNoTracking().SingleAsync();
    }
}
