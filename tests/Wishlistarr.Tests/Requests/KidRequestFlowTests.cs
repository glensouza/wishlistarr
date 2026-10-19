using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Requests;

public sealed class KidRequestFlowTests
{
    [Fact]
    public async Task Allowed_title_in_plex_is_labeled_at_once_without_a_download()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, int ada) = await factory.SignInKidAsync();

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", 603);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("labeled", body.GetProperty("state").GetString());
        Assert.Equal("The Matrix", body.GetProperty("title").GetString());
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
        OwnedLabel owned = Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal(ada, owned.KidProfileId);
        Assert.Equal("request", owned.Source);
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.False(request.CountsTowardAllowance);
        List<MediaRequestEvent> history = await factory.GetHistoryAsync(request.Id);
        Assert.Equal(
            [RequestState.Pending, RequestState.Approved, RequestState.Available, RequestState.Labeled],
            history.Select(item => item.ToState));
        Assert.DoesNotContain(history, item => item.ToState == RequestState.Fulfilling);
        Assert.Equal(RequestActorKind.Kid, history[0].ActorKind);
        Assert.All(history.Skip(1), item => Assert.Equal(RequestActorKind.System, item.ActorKind));
    }

    [Fact]
    public async Task Allowed_show_in_plex_is_labeled_on_the_series()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(tvAgeLimit: 5);

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "show", 82728);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("labeled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(["Ada"], factory.Plex.Item("201").Labels);
    }

    [Fact]
    public async Task Policy_runs_before_any_lookup_or_label()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);
        int scans = factory.LibraryScans;

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", 603);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(scans, factory.LibraryScans);
        Assert.Equal(0, factory.LabelWrites);
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Empty(await factory.GetRequestsAsync());
    }

    [Fact]
    public async Task Disallowed_titles_get_a_kid_safe_message()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", 603);
        string text = await response.Content.ReadAsStringAsync();
        JsonElement problem = JsonDocument.Parse(text).RootElement;

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("That one isn't available.", problem.GetProperty("title").GetString());
        Assert.Equal("notAllowed", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("PG-13", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rating", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Matrix", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_library_title_that_needs_approval_waits_unlabeled()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 10);

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", 129);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("pending", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(0, factory.LabelWrites);
        Assert.Empty(factory.Plex.Item("102").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Pending, request.State);
        Assert.True(request.CountsTowardAllowance);
    }

    [Fact]
    public async Task Allowed_title_missing_from_plex_waits_for_a_parent()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Pending, request.State);
        Assert.Equal("Movie 700", request.Title);
        Assert.Equal(0, factory.LabelWrites);
    }

    [Fact]
    public async Task Without_plex_allowed_titles_become_requests()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", 603);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(RequestState.Pending, Assert.Single(await factory.GetRequestsAsync()).State);
    }

    [Fact]
    public async Task Asking_twice_keeps_one_request()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(weeklyAllowance: 1);

        HttpResponseMessage first = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);
        HttpResponseMessage second = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        JsonElement merged = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, merged.GetProperty("requestCount").GetInt32());
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(request.Id, merged.GetProperty("id").GetInt32());
        Assert.Single(await factory.GetHistoryAsync(request.Id));
    }

    [Fact]
    public async Task Simultaneous_submissions_create_one_request_and_one_label()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => KidRequestApiFactory.RequestAsync(kid, "movie", 603)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode));
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(4, request.RequestCount);
        Assert.Equal(RequestState.Labeled, request.State);
        Assert.Equal(1, factory.LabelWrites);
        Assert.Single(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Weekly_allowance_stops_new_requests_but_not_instant_labels_or_repeats()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(weeklyAllowance: 2);

        Assert.Equal(HttpStatusCode.Created, (await KidRequestApiFactory.RequestAsync(kid, "movie", 700)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await KidRequestApiFactory.RequestAsync(kid, "movie", 701)).StatusCode);
        HttpResponseMessage third = await KidRequestApiFactory.RequestAsync(kid, "movie", 702);
        HttpResponseMessage instant = await KidRequestApiFactory.RequestAsync(kid, "movie", 603);
        HttpResponseMessage repeat = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        JsonElement problem = await third.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("allowanceUsed", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("resetsAt", out _));
        Assert.Equal(HttpStatusCode.Created, instant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal([700, 701, 603], (await factory.GetRequestsAsync()).Select(request => request.TmdbId));
    }

    [Fact]
    public async Task Zero_allowance_still_allows_instant_labels()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(weeklyAllowance: 0);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await KidRequestApiFactory.RequestAsync(kid, "movie", 700)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await KidRequestApiFactory.RequestAsync(kid, "movie", 603)).StatusCode);
    }

    [Fact]
    public async Task Allowance_holds_when_requests_arrive_together()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync(weeklyAllowance: 1);

        HttpResponseMessage[] responses = await Task.WhenAll(
            KidRequestApiFactory.NotInLibrary.Select(id => KidRequestApiFactory.RequestAsync(kid, "movie", id)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(3, responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Single(await factory.GetRequestsAsync());
    }

    [Fact]
    public async Task Allowance_week_starts_on_monday_in_the_parent_time_zone()
    {
        using KidRequestApiFactory factory = new();
        TimeZoneInfo chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        DateTimeOffset Local(int day, int hour) =>
            new(new DateTime(2027, 1, day, hour, 0, 0), chicago.GetUtcOffset(new DateTime(2027, 1, day, hour, 0, 0)));
        factory.SetClock(Local(3, 15));
        await factory.LinkPlexAsync();
        await factory.SetTimeZoneAsync("America/Chicago");
        (HttpClient kid, _) = await factory.SignInKidAsync(weeklyAllowance: 1);

        HttpResponseMessage sunday = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);
        factory.SetClock(Local(3, 20));
        HttpResponseMessage sundayNightMondayUtc = await KidRequestApiFactory.RequestAsync(kid, "movie", 701);
        factory.SetClock(Local(4, 0).AddMinutes(1));
        HttpResponseMessage monday = await KidRequestApiFactory.RequestAsync(kid, "movie", 702);

        Assert.Equal(HttpStatusCode.Created, sunday.StatusCode);
        Assert.Equal(DayOfWeek.Monday, Local(3, 20).UtcDateTime.DayOfWeek);
        Assert.Equal(HttpStatusCode.TooManyRequests, sundayNightMondayUtc.StatusCode);
        JsonElement problem = await sundayNightMondayUtc.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Local(4, 0), problem.GetProperty("resetsAt").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.Created, monday.StatusCode);
    }

    [Fact]
    public async Task Unknown_time_zones_are_rejected()
    {
        using KidRequestApiFactory factory = new();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await parent.PutAsJsonAsync("/api/v1/settings/parent.time_zone", new { value = "Mars/Olympus" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Library_index_is_used_when_it_can_answer()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 700, new MediaItem("102", "plex://movie/5d776b59ad5437001f79c6f8", "Movie 700", 2001, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        int scans = factory.LibraryScans;

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("labeled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(1, index.Calls);
        Assert.Equal(scans, factory.LibraryScans);
        Assert.Equal(["Ada"], factory.Plex.Item("102").Labels);
    }

    [Fact]
    public async Task Live_plex_lookup_is_used_when_the_index_is_not_ready()
    {
        FakeLibraryIndex index = new() { Ready = false };
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        int scans = factory.LibraryScans;

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", 603);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, index.Calls);
        Assert.True(factory.LibraryScans > scans);
        Assert.Equal(["Ada"], factory.Plex.Item("101").Labels);
    }

    [Fact]
    public async Task Failed_label_write_marks_the_request_failed()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 700, new MediaItem("999", "plex://movie/gone", "Gone", 2001, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        MediaRequest request = Assert.Single(await factory.GetRequestsAsync());
        Assert.Equal(RequestState.Failed, request.State);
        Assert.Equal(RequestState.Available, (await factory.GetHistoryAsync(request.Id))[^1].FromState);
    }

    [Fact]
    public async Task Bad_input_and_unknown_titles_are_rejected()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient kid, _) = await factory.SignInKidAsync();

        HttpResponseMessage missing = await kid.PostAsJsonAsync(KidRequestApiFactory.RequestsPath, new { kind = "movie" });
        HttpResponseMessage negative = await KidRequestApiFactory.RequestAsync(kid, "movie", -1);
        HttpResponseMessage unknown = await KidRequestApiFactory.RequestAsync(kid, "movie", 999999);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(await factory.GetRequestsAsync());
    }

    [Fact]
    public async Task Only_signed_in_kids_can_request()
    {
        using KidRequestApiFactory factory = new();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpClient anonymous = factory.CreateClient();

        HttpResponseMessage asParent = await KidRequestApiFactory.RequestAsync(parent, "movie", 603);
        HttpResponseMessage asNobody = await KidRequestApiFactory.RequestAsync(anonymous, "movie", 603);

        Assert.Equal(HttpStatusCode.Forbidden, asParent.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asNobody.StatusCode);
    }
}
