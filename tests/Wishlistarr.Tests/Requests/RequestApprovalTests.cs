using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Security;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Requests;

public sealed class RequestApprovalTests
{
    [Fact]
    public async Task Approving_a_title_that_is_not_in_the_library_leaves_it_approved()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = await AskAsync(kid, 700);

        JsonElement body = await DecideAsync(parent, "approve", new { ids = new[] { id } });
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        List<MediaRequestEvent> history = await factory.GetHistoryAsync(id);
        ParentAccount account = await ParentAsync(factory);

        Assert.Equal("succeeded", Item(body, id).GetProperty("status").GetString());
        Assert.Equal("approved", Item(body, id).GetProperty("state").GetString());
        Assert.Equal(RequestState.Approved, request.State);
        Assert.Equal(0, factory.LabelWrites);
        Assert.Empty(await factory.GetLedgerAsync());
        MediaRequestEvent approved = history.Single(item => item.ToState == RequestState.Approved);
        Assert.Equal(RequestState.Pending, approved.FromState);
        Assert.Equal(RequestActorKind.Parent, approved.ActorKind);
        Assert.Equal(account.Id, approved.ActorId);
        Assert.Equal(account.Username, approved.ActorName);
        Assert.DoesNotContain(history, item => item.ToState is RequestState.Fulfilling or RequestState.Available);
    }

    [Fact]
    public async Task Approving_an_in_library_title_labels_it_and_records_the_parent()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int ada) = await factory.SignInKidAsync(movieAgeLimit: 10);
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = await AskAsync(kid, 129);

        JsonElement body = await DecideAsync(parent, "approve", new { ids = new[] { id }, reason = " Fine for Ada " });

        Assert.Equal("succeeded", Item(body, id).GetProperty("status").GetString());
        Assert.Equal("labeled", Item(body, id).GetProperty("state").GetString());
        Assert.Equal(["Ada"], factory.Plex.Item("102").Labels);
        OwnedLabel owned = Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal(ada, owned.KidProfileId);
        Assert.Equal("request", owned.Source);
        List<MediaRequestEvent> history = await factory.GetHistoryAsync(id);
        Assert.Equal(
            [RequestState.Pending, RequestState.Approved, RequestState.Available, RequestState.Labeled],
            history.Select(item => item.ToState));
        Assert.Equal(RequestActorKind.Parent, history[1].ActorKind);
        Assert.Equal("Fine for Ada", history[1].Reason);
        Assert.All(history.Skip(2), item =>
        {
            Assert.Equal(RequestActorKind.System, item.ActorKind);
            Assert.Equal(RequestApproval.ApprovalComponent, item.ActorName);
            Assert.Null(item.ActorId);
        });
        Assert.DoesNotContain(history, item => item.ToState == RequestState.Fulfilling);
    }

    [Fact]
    public async Task The_library_index_decides_whether_an_approval_labels_the_title()
    {
        FakeLibraryIndex empty = new();
        using KidRequestApiFactory missing = new(empty);
        await missing.LinkPlexAsync();
        (HttpClient waiting, _) = await missing.SignInKidAsync(movieAgeLimit: 10);
        int waitingId = await AskAsync(waiting, 129);
        JsonElement notHere = await DecideAsync(await missing.CreateSignedInClientAsync(), "approve", new { ids = new[] { waitingId } });

        Assert.True(empty.Calls > 0);
        Assert.Equal("succeeded", Item(notHere, waitingId).GetProperty("status").GetString());
        Assert.Equal("approved", Item(notHere, waitingId).GetProperty("state").GetString());
        Assert.Equal(0, missing.LabelWrites);
        Assert.Empty(missing.Plex.Item("102").Labels);

        FakeLibraryIndex indexed = new();
        indexed.Add(MediaKind.Movie, 129, new MediaItem("102", "plex://movie/5d776b59ad5437001f79c6f8", "Spirited Away", 2001, MediaKind.Movie, "1", []));
        using KidRequestApiFactory present = new(indexed);
        await present.LinkPlexAsync();
        (HttpClient kid, _) = await present.SignInKidAsync("Bea", movieAgeLimit: 10);
        int id = await AskAsync(kid, 129);
        JsonElement labeled = await DecideAsync(await present.CreateSignedInClientAsync(), "approve", new { ids = new[] { id } });

        Assert.True(indexed.Calls > 0);
        Assert.Equal("labeled", Item(labeled, id).GetProperty("state").GetString());
        Assert.Equal(["Bea"], present.Plex.Item("102").Labels);
        Assert.Single(await present.GetLedgerAsync());
    }

    [Fact]
    public async Task A_missing_library_item_fails_the_approval_without_a_label()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 129, new MediaItem("999", "plex://movie/missing", "Gone", null, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = await AskAsync(kid, 129);

        JsonElement body = await DecideAsync(parent, "approve", new { ids = new[] { id } });
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());

        Assert.Equal("failed", Item(body, id).GetProperty("status").GetString());
        Assert.Equal(RequestState.Failed, request.State);
        Assert.Empty(factory.Plex.Item("102").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Contains(await factory.GetHistoryAsync(id), item => item.ActorKind == RequestActorKind.Parent && item.ToState == RequestState.Approved);
    }

    [Fact]
    public async Task An_unknown_library_lookup_does_not_approve_or_label()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = await AskAsync(kid, 700);

        JsonElement body = await DecideAsync(parent, "approve", new { ids = new[] { id } });

        Assert.Equal("failed", Item(body, id).GetProperty("status").GetString());
        Assert.Equal("pending", Item(body, id).GetProperty("state").GetString());
        Assert.Equal(RequestState.Pending, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(0, factory.LabelWrites);
    }

    [Fact]
    public async Task Api_key_callers_are_recorded_without_an_account()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient keyed = await factory.CreateAuthorizedClientAsync();
        int id = await AskAsync(kid, 700);

        await DecideAsync(keyed, "approve", new { ids = new[] { id } });
        MediaRequestEvent approved = (await factory.GetHistoryAsync(id)).Single(item => item.ToState == RequestState.Approved);

        Assert.Equal(RequestActorKind.Parent, approved.ActorKind);
        Assert.Null(approved.ActorId);
        Assert.Equal("API key", approved.ActorName);
    }

    [Fact]
    public async Task Denial_stores_a_trimmed_plain_text_reason_and_shows_it_only_to_that_kid()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient ada, _) = await factory.SignInKidAsync("Ada");
        (HttpClient ben, _) = await factory.SignInKidAsync("Ben");
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = await AskAsync(ada, 700);
        string reason = "  <b>Not tonight</b>  ";

        JsonElement denied = await DecideAsync(parent, "deny", new { ids = new[] { id }, reason });
        HttpResponseMessage adaList = await ada.GetAsync("/api/v1/kid/requests");
        string adaJson = await adaList.Content.ReadAsStringAsync();
        HttpResponseMessage benList = await ben.GetAsync("/api/v1/kid/requests");
        string benJson = await benList.Content.ReadAsStringAsync();
        JsonElement parentList = await (await parent.GetAsync("/api/v1/requests?state=denied")).Content.ReadFromJsonAsync<JsonElement>();
        MediaRequestEvent denial = (await factory.GetHistoryAsync(id)).Single(item => item.ToState == RequestState.Denied);

        Assert.Equal("succeeded", Item(denied, id).GetProperty("status").GetString());
        Assert.Equal("application/json", adaList.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<b>Not tonight</b>", denial.Reason);
        JsonElement own = Assert.Single(JsonDocument.Parse(adaJson).RootElement.EnumerateArray());
        Assert.Equal(id, own.GetProperty("id").GetInt32());
        Assert.Equal("<b>Not tonight</b>", own.GetProperty("denialReason").GetString());
        Assert.False(own.TryGetProperty("actorName", out _));
        Assert.False(own.TryGetProperty("actorId", out _));
        Assert.False(own.TryGetProperty("kidName", out _));
        Assert.DoesNotContain("parent", adaJson, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(JsonDocument.Parse(benJson).RootElement.EnumerateArray());
        Assert.DoesNotContain("Not tonight", benJson, StringComparison.Ordinal);
        Assert.Equal("<b>Not tonight</b>", Assert.Single(parentList.EnumerateArray()).GetProperty("denialReason").GetString());
    }

    [Fact]
    public async Task A_blank_denial_reason_is_stored_as_nothing_and_a_long_one_is_rejected()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int blankId = await AskAsync(kid, 700);
        int longId = await AskAsync(kid, 701);

        JsonElement blank = await DecideAsync(parent, "deny", new { ids = new[] { blankId }, reason = "   " });
        HttpResponseMessage tooLong = await parent.PostAsJsonAsync(
            "/api/v1/requests/deny",
            new { ids = new[] { longId }, reason = new string('n', MediaRequests.MaxReasonLength + 1) });
        List<JsonElement> ownList = await kid.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid/requests") ?? [];
        JsonElement own = Assert.Single(ownList, item => item.GetProperty("id").GetInt32() == blankId);
        JsonElement waiting = Assert.Single(ownList, item => item.GetProperty("id").GetInt32() == longId);

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("succeeded", Item(blank, blankId).GetProperty("status").GetString());
        Assert.Null((await factory.GetHistoryAsync(blankId)).Single(item => item.ToState == RequestState.Denied).Reason);
        Assert.Equal(JsonValueKind.Null, own.GetProperty("denialReason").ValueKind);
        Assert.Equal("pending", waiting.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("denialReason").ValueKind);
        Assert.Equal(RequestState.Pending, (await factory.GetRequestsAsync()).Single(request => request.Id == longId).State);
    }

    [Fact]
    public async Task Failure_text_is_not_shown_on_the_kid_request_list()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 129, new MediaItem("999", "plex://movie/missing", "Gone", null, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        int id = await AskAsync(kid, 129);
        await DecideAsync(await factory.CreateSignedInClientAsync(), "approve", new { ids = new[] { id } });

        string json = await (await kid.GetAsync("/api/v1/kid/requests")).Content.ReadAsStringAsync();
        JsonElement own = Assert.Single(JsonDocument.Parse(json).RootElement.EnumerateArray());

        Assert.Equal("failed", own.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, own.GetProperty("denialReason").ValueKind);
        Assert.DoesNotContain("library", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("label", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bulk_decisions_report_each_id_and_a_second_call_does_not_apply_again()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int first = await AskAsync(kid, 700);
        int second = await AskAsync(kid, 701);
        int denied = await AskAsync(kid, 702);
        await DecideAsync(parent, "deny", new { ids = new[] { denied }, reason = "Too scary" });

        JsonElement mixed = await DecideAsync(parent, "approve", new { ids = new[] { first, second, denied, 9999 } });
        int writes = factory.LabelWrites;
        JsonElement again = await DecideAsync(parent, "approve", new { ids = new[] { first } });
        JsonElement denyAgain = await DecideAsync(parent, "deny", new { ids = new[] { denied }, reason = "A different note" });

        Assert.Equal("succeeded", Item(mixed, first).GetProperty("status").GetString());
        Assert.Equal("succeeded", Item(mixed, second).GetProperty("status").GetString());
        Assert.Equal("conflict", Item(mixed, denied).GetProperty("status").GetString());
        Assert.Equal("denied", Item(mixed, denied).GetProperty("state").GetString());
        Assert.Equal("notFound", Item(mixed, 9999).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, Item(mixed, 9999).GetProperty("state").ValueKind);
        Assert.Equal("alreadyDecided", Item(again, first).GetProperty("status").GetString());
        Assert.Equal("approved", Item(again, first).GetProperty("state").GetString());
        Assert.Equal(writes, factory.LabelWrites);
        Assert.Equal("alreadyDecided", Item(denyAgain, denied).GetProperty("status").GetString());
        Assert.Equal("Too scary", (await factory.GetHistoryAsync(denied)).Single(item => item.ToState == RequestState.Denied).Reason);
        Assert.Single(await factory.GetHistoryAsync(first), item => item.ToState == RequestState.Approved);
    }

    [Fact]
    public async Task A_batch_over_the_cap_or_with_bad_ids_changes_nothing()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = await AskAsync(kid, 700);
        int[] tooMany = [.. Enumerable.Range(1, RequestApproval.MaxBatch + 1)];

        HttpResponseMessage over = await parent.PostAsJsonAsync("/api/v1/requests/approve", new { ids = tooMany });
        HttpResponseMessage zero = await parent.PostAsJsonAsync("/api/v1/requests/deny", new { ids = new[] { 0, id } });
        HttpResponseMessage empty = await parent.PostAsJsonAsync("/api/v1/requests/approve", new { ids = Array.Empty<int>() });
        HttpResponseMessage kidCall = await kid.PostAsJsonAsync("/api/v1/requests/deny", new { ids = new[] { id }, reason = "no" });
        HttpClient unsigned = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await unsigned.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword })).StatusCode);
        HttpResponseMessage noToken = await unsigned.PostAsJsonAsync("/api/v1/requests/approve", new { ids = new[] { id } });

        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, kidCall.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(RequestState.Pending, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync("/api/v1/requests")).StatusCode);
    }

    [Fact]
    public async Task Two_parents_cannot_both_decide_the_same_request()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        HttpClient first = await factory.CreateSignedInClientAsync();
        HttpClient second = await factory.CreateSignedInClientAsync();
        int id = await AskAsync(kid, 129);

        JsonElement[] results = await Task.WhenAll(
            DecideAsync(first, "approve", new { ids = new[] { id } }),
            DecideAsync(second, "deny", new { ids = new[] { id }, reason = "No" }));

        JsonElement winner = Assert.Single(results, body => Item(body, id).GetProperty("status").GetString() == "succeeded");
        JsonElement loser = Assert.Single(results, body => body.ToString() != winner.ToString());
        string loserStatus = Item(loser, id).GetProperty("status").GetString()!;
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        int labels = (await factory.GetLedgerAsync()).Count;

        Assert.True(loserStatus is "conflict" or "alreadyDecided");
        Assert.NotEqual("succeeded", loserStatus);
        if (Item(winner, id).GetProperty("state").GetString() == "labeled")
        {
            Assert.Equal(RequestState.Labeled, request.State);
            Assert.Equal(1, labels);
            Assert.Equal(["Ada"], factory.Plex.Item("102").Labels);
        }
        else
        {
            Assert.Equal(RequestState.Denied, request.State);
            Assert.Equal(0, labels);
            Assert.Empty(factory.Plex.Item("102").Labels);
        }
    }

    [Fact]
    public async Task Two_approvals_of_an_in_library_title_label_it_once()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        HttpClient first = await factory.CreateSignedInClientAsync();
        HttpClient second = await factory.CreateSignedInClientAsync();
        int id = await AskAsync(kid, 129);

        JsonElement[] results = await Task.WhenAll(
            DecideAsync(first, "approve", new { ids = new[] { id } }),
            DecideAsync(second, "approve", new { ids = new[] { id } }));

        Assert.Single(results, body => Item(body, id).GetProperty("status").GetString() == "succeeded");
        Assert.All(
            results.Where(body => Item(body, id).GetProperty("status").GetString() != "succeeded"),
            body => Assert.True(Item(body, id).GetProperty("status").GetString() is "alreadyDecided" or "conflict"));
        Assert.Equal(["Ada"], factory.Plex.Item("102").Labels);
        Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal(RequestState.Labeled, Assert.Single(await factory.GetRequestsAsync()).State);
    }

    [Fact]
    public async Task The_profile_switch_auto_approves_through_the_same_path_as_the_system()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int ada) = await factory.SignInKidAsync(autoApprove: true, weeklyAllowance: 0);
        HttpResponseMessage downloaded = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);
        HttpResponseMessage ready = await KidRequestApiFactory.RequestAsync(kid, "movie", 129);

        Assert.Equal(HttpStatusCode.TooManyRequests, downloaded.StatusCode);
        Assert.Equal(HttpStatusCode.Created, ready.StatusCode);
        Assert.Equal("labeled", (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(129, request.TmdbId);
        Assert.Equal(RequestState.Labeled, request.State);
        Assert.False(request.CountsTowardAllowance);
        List<MediaRequestEvent> history = await factory.GetHistoryAsync(request.Id);
        Assert.Equal(RequestActorKind.Kid, history[0].ActorKind);
        Assert.All(history.Skip(1), item =>
        {
            Assert.Equal(RequestActorKind.System, item.ActorKind);
            Assert.Equal("auto-approve", item.ActorName);
        });
        Assert.Equal(RequestApproval.AutoApproveReason, history[1].Reason);
        Assert.Equal(["Ada"], factory.Plex.Item("102").Labels);
        Assert.Equal(ada, Assert.Single(await factory.GetLedgerAsync()).KidProfileId);
        Assert.DoesNotContain(await factory.GetRequestsAsync(), request => request.TmdbId == 700);
    }

    [Fact]
    public async Task A_rating_ceiling_auto_approves_up_to_that_level_and_leaves_the_rest_pending()
    {
        using KidRequestApiFactory ceiling = new();
        await ceiling.LinkPlexAsync();
        (HttpClient kid, _) = await ceiling.SignInKidAsync(autoApproveMovieRating: "parentalGuidance");
        HttpResponseMessage within = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);
        HttpResponseMessage unrated = await KidRequestApiFactory.RequestAsync(kid, "movie", 129);

        Assert.Equal(HttpStatusCode.Created, within.StatusCode);
        Assert.Equal("approved", (await within.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(RequestState.Approved, (await ceiling.GetRequestsAsync()).Single(request => request.TmdbId == 700).State);
        Assert.Equal(RequestActorKind.System, (await ceiling.GetHistoryAsync((await ceiling.GetRequestsAsync()).Single(request => request.TmdbId == 700).Id))
            .Single(item => item.ToState == RequestState.Approved).ActorKind);
        Assert.Equal(HttpStatusCode.Created, unrated.StatusCode);
        Assert.Equal("pending", (await unrated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(0, ceiling.LabelWrites);

        using KidRequestApiFactory lower = new();
        await lower.LinkPlexAsync();
        (HttpClient stricter, _) = await lower.SignInKidAsync("Bea", autoApproveMovieRating: "general");
        HttpResponseMessage above = await KidRequestApiFactory.RequestAsync(stricter, "movie", 700);

        Assert.Equal("pending", (await above.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Unknown_genres_are_not_auto_approved()
    {
        using KidRequestApiFactory ceiling = new();
        ceiling.Tmdb.SetBody("/3/movie/603", """{"id":603,"title":"The Matrix","genres":[],"original_language":"en","vote_average":8.2}""");
        await ceiling.LinkPlexAsync();
        (HttpClient kid, _) = await ceiling.SignInKidAsync(blockedGenreIds: [16], autoApproveMovieRating: "teen");
        HttpResponseMessage rated = await KidRequestApiFactory.RequestAsync(kid, "movie", 603);

        using KidRequestApiFactory enabled = new();
        enabled.Tmdb.SetBody("/3/movie/603", """{"id":603,"title":"The Matrix","genres":[],"original_language":"en","vote_average":8.2}""");
        await enabled.LinkPlexAsync();
        (HttpClient switched, _) = await enabled.SignInKidAsync("Bea", blockedGenreIds: [16], autoApprove: true);
        HttpResponseMessage any = await KidRequestApiFactory.RequestAsync(switched, "movie", 603);

        Assert.Equal("pending", (await rated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal("pending", (await any.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(0, ceiling.LabelWrites);
        Assert.Equal(0, enabled.LabelWrites);
        Assert.Empty(ceiling.Plex.Item("101").Labels);
        Assert.Empty(enabled.Plex.Item("101").Labels);
    }

    [Fact]
    public async Task Unknown_genres_stay_pending_when_the_title_is_also_unrated()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(blockedGenreIds: [16], autoApprove: true);
        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", 129);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("pending", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(RequestState.Pending, Assert.Single(await factory.GetRequestsAsync()).State);
        Assert.Equal(0, factory.LabelWrites);
        Assert.Empty(factory.Plex.Item("102").Labels);
    }

    [Fact]
    public async Task Auto_approve_does_not_let_a_blocked_title_through()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10, autoApprove: true, autoApproveMovieRating: "adult");

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", 603);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await factory.GetRequestsAsync());
        Assert.Empty(factory.Plex.Item("101").Labels);
    }

    private static async Task<int> AskAsync(HttpClient kid, int tmdbId)
    {
        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", tmdbId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> DecideAsync(HttpClient parent, string action, object body)
    {
        HttpResponseMessage response = await parent.PostAsJsonAsync($"/api/v1/requests/{action}", body);
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
