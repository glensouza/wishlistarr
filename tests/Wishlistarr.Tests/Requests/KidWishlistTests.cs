using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Tests.Plex;

namespace Wishlistarr.Tests.Requests;

public sealed class KidWishlistTests
{
    private static readonly string[] ResponseFields = ["remaining", "resetsAt", "items"];
    private static readonly string[] ItemFields = ["id", "kind", "tmdbId", "title", "state", "note", "plex"];
    private static readonly string[] PlexFields = ["webUrl", "appUrl"];

    [Fact]
    public async Task A_kid_sees_only_their_own_requests_and_no_parent_fields()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient ada, int adaId) = await factory.SignInKidAsync("Ada");
        (HttpClient beau, int beauId) = await factory.SignInKidAsync("Beau");
        await AddRequestAsync(factory, adaId, 710, "Ada Movie");
        await AddRequestAsync(factory, beauId, 711, "Beau Secret Show", kind: MediaKind.Show);
        await AddRequestAsync(factory, adaId, 712, "Ada Newer");

        HttpResponseMessage response = await ada.GetAsync(KidRequestApiFactory.RequestsPath + "?profileId=" + beauId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl is { NoStore: true, Private: true });
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ResponseFields, Names(body));
        JsonElement[] items = [.. body.GetProperty("items").EnumerateArray()];
        Assert.Equal(["Ada Newer", "Ada Movie"], items.Select(item => item.GetProperty("title").GetString()));
        Assert.All(items, item => Assert.Equal(ItemFields, Names(item)));
        Assert.DoesNotContain("Beau Secret Show", body.GetRawText(), StringComparison.Ordinal);

        JsonElement beauBody = await (await beau.GetAsync(KidRequestApiFactory.RequestsPath)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["Beau Secret Show"], beauBody.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("title").GetString()));
        Assert.DoesNotContain("Ada Movie", beauBody.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Friendly_states_cover_every_internal_state_and_only_a_parent_denial_has_a_note()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient ada, int adaId) = await factory.SignInKidAsync();
        const string ParentNote = "<b>Maybe a cartoon</b> <img src=x onerror=alert(1)>";
        const string SystemReason = "Download client said the path was missing";
        const string ApprovalReason = "Allowed for the birthday";

        await AddRequestAsync(factory, adaId, 720, "Still Waiting");
        int failed = await AddRequestAsync(factory, adaId, 721, "Needs A Parent");
        await MoveAsync(factory, failed, RequestState.Approved, RequestActor.Parent(null, "Parent"), ApprovalReason);
        await MoveAsync(factory, failed, RequestState.Failed, RequestActor.System("dispatch"), SystemReason);
        int approved = await AddRequestAsync(factory, adaId, 722, "Parent Said Yes");
        await MoveAsync(factory, approved, RequestState.Approved, RequestActor.Parent(null, "Parent"), ApprovalReason);
        int fulfilling = await AddRequestAsync(factory, adaId, 723, "Downloading");
        await MoveAsync(factory, fulfilling, RequestState.Approved, RequestActor.Parent(null, "Parent"));
        await MoveAsync(factory, fulfilling, RequestState.Fulfilling, RequestActor.System("dispatch"));
        int available = await AddRequestAsync(factory, adaId, 724, "On The Server");
        await MoveAsync(factory, available, RequestState.Approved, RequestActor.Parent(null, "Parent"));
        await MoveAsync(factory, available, RequestState.Available, RequestActor.System("dispatch"));
        int labeled = await AddRequestAsync(factory, adaId, 725, "Labeled Film");
        await MoveAsync(factory, labeled, RequestState.Approved, RequestActor.Parent(null, "Parent"));
        await MoveAsync(factory, labeled, RequestState.Available, RequestActor.System("dispatch"));
        await MoveAsync(factory, labeled, RequestState.Labeled, RequestActor.System("dispatch"));
        int denied = await AddRequestAsync(factory, adaId, 726, "Not Tonight");
        await MoveAsync(factory, denied, RequestState.Denied, RequestActor.Parent(null, "Parent"), "  " + ParentNote + "  ");
        int systemDenied = await AddRequestAsync(factory, adaId, 727, "System Said No");
        await MoveAsync(factory, systemDenied, RequestState.Denied, RequestActor.System("policy"), SystemReason);

        JsonElement body = await (await ada.GetAsync(KidRequestApiFactory.RequestsPath)).Content.ReadFromJsonAsync<JsonElement>();
        string text = body.GetRawText();

        Assert.Equal("waiting", Item(body, "Still Waiting").GetProperty("state").GetString());
        Assert.Equal("waiting", Item(body, "Needs A Parent").GetProperty("state").GetString());
        Assert.Equal("onTheWay", Item(body, "Parent Said Yes").GetProperty("state").GetString());
        Assert.Equal("onTheWay", Item(body, "Downloading").GetProperty("state").GetString());
        Assert.Equal("ready", Item(body, "On The Server").GetProperty("state").GetString());
        Assert.Equal("ready", Item(body, "Labeled Film").GetProperty("state").GetString());
        Assert.Equal("denied", Item(body, "Not Tonight").GetProperty("state").GetString());
        Assert.Equal(ParentNote, Item(body, "Not Tonight").GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, Item(body, "System Said No").GetProperty("note").ValueKind);
        Assert.Equal(JsonValueKind.Null, Item(body, "Parent Said Yes").GetProperty("note").ValueKind);
        Assert.All(body.GetProperty("items").EnumerateArray(), item => Assert.Equal(JsonValueKind.Null, item.GetProperty("plex").ValueKind));
        Assert.DoesNotContain(SystemReason, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ApprovalReason, text, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ready_items_link_to_plex_without_a_server_address_or_token()
    {
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 730, new MediaItem("102", "plex://movie/abc", "Ready Film", 2001, MediaKind.Movie, "1", []));
        index.Add(MediaKind.Show, 731, new MediaItem("201", "plex://show/abc", "Ready Show", 2018, MediaKind.Show, "2", []));
        index.Add(MediaKind.Movie, 732, new MediaItem("10/2", "plex://movie/bad", "Bad Key", 2002, MediaKind.Movie, "1", []));
        index.Add(MediaKind.Movie, 733, new MediaItem("104", "plex://movie/pending", "Not Ready", 2003, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (HttpClient ada, int adaId) = await factory.SignInKidAsync();
        await ReachAsync(factory, adaId, 730, "Ready Film", RequestState.Labeled);
        await ReachAsync(factory, adaId, 731, "Ready Show", RequestState.Available, MediaKind.Show);
        await ReachAsync(factory, adaId, 732, "Bad Key", RequestState.Labeled);
        await AddRequestAsync(factory, adaId, 733, "Not Ready");

        using IServiceScope scope = factory.Services.CreateScope();
        SettingsStore settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        string token = await settings.GetAsync("plex.server_token") ?? string.Empty;
        string serverUrl = await settings.GetAsync("plex.server_url") ?? string.Empty;
        Assert.NotEmpty(token);
        Assert.NotEmpty(serverUrl);

        HttpResponseMessage response = await ada.GetAsync(KidRequestApiFactory.RequestsPath);
        string text = await response.Content.ReadAsStringAsync();
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(text);
        string machine = FakePlexServer.MachineIdentifier;

        JsonElement film = Item(body, "Ready Film").GetProperty("plex");
        Assert.Equal(PlexFields, Names(film));
        Assert.Equal(
            $"https://app.plex.tv/desktop/#!/server/{machine}/details?key=%2Flibrary%2Fmetadata%2F102",
            film.GetProperty("webUrl").GetString());
        Assert.Equal(
            $"plex://preplay/?metadataKey=%2Flibrary%2Fmetadata%2F102&metadataType=1&server={machine}",
            film.GetProperty("appUrl").GetString());
        JsonElement show = Item(body, "Ready Show").GetProperty("plex");
        Assert.Equal(
            $"plex://preplay/?metadataKey=%2Flibrary%2Fmetadata%2F201&metadataType=2&server={machine}",
            show.GetProperty("appUrl").GetString());
        Assert.Equal(JsonValueKind.Null, Item(body, "Bad Key").GetProperty("plex").ValueKind);
        Assert.Equal(JsonValueKind.Null, Item(body, "Not Ready").GetProperty("plex").ValueKind);
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(serverUrl, text, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Plex-Token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10/2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ready_item_has_no_link_when_the_library_cannot_answer()
    {
        FakeLibraryIndex index = new() { Ready = false };
        index.Add(MediaKind.Movie, 740, new MediaItem("102", "plex://movie/abc", "Ready Film", 2001, MediaKind.Movie, "1", []));
        using KidRequestApiFactory factory = new(index);
        await factory.LinkPlexAsync();
        (HttpClient ada, int adaId) = await factory.SignInKidAsync();
        await ReachAsync(factory, adaId, 740, "Ready Film", RequestState.Labeled);

        JsonElement body = await (await ada.GetAsync(KidRequestApiFactory.RequestsPath)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ready", Item(body, "Ready Film").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, Item(body, "Ready Film").GetProperty("plex").ValueKind);
        Assert.DoesNotContain("app.plex.tv", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remaining_wishes_follow_the_weekly_allowance()
    {
        using KidRequestApiFactory factory = new();
        await factory.SetTimeZoneAsync("UTC");
        factory.SetClock(new DateTimeOffset(2026, 10, 21, 15, 0, 0, TimeSpan.Zero));
        (HttpClient ada, int adaId) = await factory.SignInKidAsync(weeklyAllowance: 2);
        (HttpClient beau, int beauId) = await factory.SignInKidAsync("Beau", weeklyAllowance: 1);
        await AddRequestAsync(factory, adaId, 750, "Counts One");
        await AddRequestAsync(factory, adaId, 751, "Counts Two");
        await AddRequestAsync(factory, adaId, 752, "Already Here", counts: false);
        await AddRequestAsync(factory, beauId, 753, "Beaus Wish");

        JsonElement usedUp = await (await ada.GetAsync(KidRequestApiFactory.RequestsPath)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, usedUp.GetProperty("remaining").GetInt32());
        Assert.Equal(
            AllowanceWeek.NextStart(factory.Clock.GetUtcNow(), TimeZoneInfo.Utc),
            usedUp.GetProperty("resetsAt").GetDateTimeOffset());
        JsonElement beauUsed = await (await beau.GetAsync(KidRequestApiFactory.RequestsPath)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, beauUsed.GetProperty("remaining").GetInt32());

        // Monday 26 October is the next allowance week and is still inside the seven-day kid session.
        factory.SetClock(new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.Zero));
        JsonElement nextWeek = await (await ada.GetAsync(KidRequestApiFactory.RequestsPath)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, nextWeek.GetProperty("remaining").GetInt32());
        JsonElement beauNext = await (await beau.GetAsync(KidRequestApiFactory.RequestsPath)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, beauNext.GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task No_allowance_reports_no_limit_and_zero_reports_none_left()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient open, _) = await factory.SignInKidAsync("Ada");
        (HttpClient none, _) = await factory.SignInKidAsync("Beau", weeklyAllowance: 0);

        JsonElement unlimited = await (await open.GetAsync(KidRequestApiFactory.RequestsPath)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, unlimited.GetProperty("remaining").ValueKind);
        Assert.Equal(JsonValueKind.Null, unlimited.GetProperty("resetsAt").ValueKind);

        JsonElement blocked = await (await none.GetAsync(KidRequestApiFactory.RequestsPath)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, blocked.GetProperty("remaining").GetInt32());
        Assert.Equal(JsonValueKind.String, blocked.GetProperty("resetsAt").ValueKind);
    }

    [Fact]
    public async Task A_parent_and_an_anonymous_caller_cannot_read_the_wishlist()
    {
        using KidRequestApiFactory factory = new();
        await factory.SignInKidAsync();
        HttpClient anonymous = factory.CreateClient();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(KidRequestApiFactory.RequestsPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await parent.GetAsync(KidRequestApiFactory.RequestsPath)).StatusCode);
    }

    private static JsonElement Item(JsonElement body, string title) =>
        body.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("title").GetString() == title);

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name)];

    private static async Task<int> AddRequestAsync(
        KidRequestApiFactory factory,
        int profileId,
        int tmdbId,
        string title,
        bool counts = true,
        MediaKind kind = MediaKind.Movie)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        RequestResult result = await scope.ServiceProvider.GetRequiredService<MediaRequests>().CreateAsync(
            profileId,
            new TitleKey(kind, tmdbId),
            title,
            RequestActor.Kid(profileId, "Kid"),
            counts);
        Assert.Equal(RequestOutcome.Created, result.Outcome);
        return result.Request!.Id;
    }

    private static async Task MoveAsync(KidRequestApiFactory factory, int requestId, RequestState to, RequestActor actor, string? reason = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        RequestResult result = await scope.ServiceProvider.GetRequiredService<MediaRequests>().TransitionAsync(requestId, to, actor, reason);
        Assert.Equal(RequestOutcome.Changed, result.Outcome);
    }

    private static async Task ReachAsync(
        KidRequestApiFactory factory,
        int profileId,
        int tmdbId,
        string title,
        RequestState target,
        MediaKind kind = MediaKind.Movie)
    {
        int id = await AddRequestAsync(factory, profileId, tmdbId, title, kind: kind);
        await MoveAsync(factory, id, RequestState.Approved, RequestActor.Parent(null, "Parent"));
        if (target == RequestState.Approved)
        {
            return;
        }

        await MoveAsync(factory, id, target == RequestState.Labeled ? RequestState.Available : target, RequestActor.System("dispatch"));
        if (target == RequestState.Labeled)
        {
            await MoveAsync(factory, id, RequestState.Labeled, RequestActor.System("dispatch"));
        }
    }
}
