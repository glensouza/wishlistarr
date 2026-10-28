using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Dtdd;
using Wishlistarr.Tests;
using Wishlistarr.Tests.Dtdd;

namespace Wishlistarr.Tests.Requests;

public sealed class ContentAdvisoryTests
{
    private const string QueuePath = "/api/v1/requests/queue";
    private const string NoteText = "PARENT-NOTE-ZEBRA";
    private const string TopicName = "TOPIC-ZEBRA-DIES";

    [Fact]
    public async Task Queue_without_a_key_keeps_descriptors_and_makes_no_warning_call()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetBody("/3/movie/703/release_dates", """
            {"results":[{"iso_3166_1":"US","release_dates":[{"certification":"PG-13","descriptors":["Violence"],"type":3}]}]}
            """);
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 13);
        await AskAsync(kid, 703);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await parent.GetAsync(QueuePath);
        string raw = await response.Content.ReadAsStringAsync();
        JsonElement item = Assert.Single(JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Violence"], Texts(item.GetProperty("descriptors")));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("contentWarnings").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("attribution").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("parentNote").ValueKind);
        Assert.DoesNotContain("contentWarnings", Texts(item.GetProperty("gaps")));
        Assert.DoesNotContain("http", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("doesthedogdie", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(factory.Dtdd.Requests);
    }

    [Fact]
    public async Task Queue_shows_majority_topics_and_attribution_and_reuses_the_cache_until_the_key_changes()
    {
        using KidRequestApiFactory factory = new();
        PrepareMovie(factory, 703);
        factory.Dtdd.SetList(703, MovieList(15, 703));
        factory.Dtdd.SetDetail(15, Topics(
            (TopicName, 8, 1),
            ("tied vote", 3, 3),
            ("quiet", 0, 4),
            ("second warning", 5, 1)));
        (HttpClient kid, int first) = await factory.SignInKidAsync("Ada", movieAgeLimit: 10);
        (HttpClient other, int second) = await factory.SignInKidAsync("Bea", movieAgeLimit: 10);
        await AskAsync(kid, 703);
        await AskAsync(other, 703);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        await SaveKeyAsync(parent, FakeDtddServer.ValidApiKey);

        JsonElement page = await QueueAsync(parent);
        int calls = factory.Dtdd.Count("/api/v3/items");
        JsonElement again = await QueueAsync(parent);

        Assert.True(calls > 0);
        Assert.Equal(calls, factory.Dtdd.Count("/api/v3/items"));
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.All(page.GetProperty("items").EnumerateArray(), item => AssertTopics(item, Note: null));
        Assert.All(again.GetProperty("items").EnumerateArray(), item => AssertTopics(item, Note: null));

        HttpResponseMessage saved = await parent.PutAsJsonAsync("/api/v1/titles/movie/703/note", new { note = "  " + NoteText + "  " });
        JsonElement noted = await saved.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement withNote = await QueueAsync(parent);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(NoteText, noted.GetProperty("note").GetString());
        Assert.All(withNote.GetProperty("items").EnumerateArray(), item => Assert.Equal(NoteText, item.GetProperty("parentNote").GetString()));
        Assert.Contains(first, withNote.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("kidProfileId").GetInt32()));
        Assert.Contains(second, withNote.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("kidProfileId").GetInt32()));

        Assert.Equal(HttpStatusCode.NoContent, (await parent.PutAsJsonAsync($"/api/v1/settings/{DtddOptions.ApiKeySetting}", new { value = " " })).StatusCode);
        JsonElement cleared = await QueueAsync(parent);
        int afterDelete = factory.Dtdd.Count("/api/v3/items");
        Assert.Equal(calls, afterDelete);
        Assert.All(cleared.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal(JsonValueKind.Null, item.GetProperty("contentWarnings").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("attribution").ValueKind);
            Assert.Equal(NoteText, item.GetProperty("parentNote").GetString());
        });

        factory.Dtdd.SetDetail(15, Topics(("fresh warning", 4, 0)));
        await SaveKeyAsync(parent, FakeDtddServer.ValidApiKey);
        JsonElement refreshed = await QueueAsync(parent);
        Assert.True(factory.Dtdd.Count("/api/v3/items") > afterDelete);
        Assert.All(refreshed.GetProperty("items").EnumerateArray(), item => Assert.Equal(["fresh warning"], Texts(item.GetProperty("contentWarnings"))));
    }

    [Fact]
    public async Task A_rejected_or_limited_lookup_is_a_gap_and_approval_still_works()
    {
        using KidRequestApiFactory factory = new();
        PrepareMovie(factory, 703);
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 13);
        int id = await AskAsync(kid, 703);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        await SaveKeyAsync(parent, FakeDtddServer.ValidApiKey);
        factory.Dtdd.Enqueue("/api/v3/items", 401, body: """{"error":"invalid_api_key","message":"DO-NOT-LOG-THIS"}""");

        JsonElement denied = Assert.Single((await QueueAsync(parent)).GetProperty("items").EnumerateArray());
        Assert.Equal(["contentWarnings"], Texts(denied.GetProperty("gaps")));
        Assert.Equal(JsonValueKind.Null, denied.GetProperty("contentWarnings").ValueKind);
        Assert.Equal(JsonValueKind.Null, denied.GetProperty("attribution").ValueKind);
        Assert.Equal(["Violence"], Texts(denied.GetProperty("descriptors")));
        Assert.DoesNotContain("DO-NOT-LOG-THIS", denied.ToString(), StringComparison.Ordinal);

        for (int index = 0; index < 6; index++)
        {
            factory.Dtdd.Enqueue("/api/v3/items", 429, "1", """{"error":"rate_limit_exceeded","message":"DO-NOT-LOG-THIS"}""");
        }

        JsonElement limited = Assert.Single((await QueueAsync(parent)).GetProperty("items").EnumerateArray());
        Assert.Contains("contentWarnings", Texts(limited.GetProperty("gaps")));
        Assert.Equal(JsonValueKind.Null, limited.GetProperty("attribution").ValueKind);
        HttpResponseMessage approved = await parent.PostAsJsonAsync("/api/v1/requests/approve", new { ids = new[] { id } });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
    }

    [Fact]
    public async Task A_parent_note_is_rejected_when_too_long_cleared_when_empty_and_hidden_from_kids_and_devices()
    {
        using KidRequestApiFactory factory = new();
        PrepareMovie(factory, 703);
        factory.Dtdd.SetList(703, MovieList(15, 703));
        factory.Dtdd.SetDetail(15, Topics((TopicName, 6, 1)));
        (HttpClient kid, int profileId) = await factory.SignInKidAsync(movieAgeLimit: 13);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpClient device = await factory.PairDeviceAsync(parent, "Tablet", [profileId]);
        await SaveKeyAsync(parent, FakeDtddServer.ValidApiKey);
        await AskAsync(kid, 703);
        await parent.PutAsJsonAsync("/api/v1/titles/movie/703/note", new { note = NoteText });

        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PutAsJsonAsync("/api/v1/titles/movie/703/note", new { note = new string('n', 2001) })).StatusCode);
        Assert.Equal(NoteText, (await parent.GetFromJsonAsync<JsonElement>("/api/v1/titles/movie/703/note")).GetProperty("note").GetString());
        Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync("/api/v1/titles/movie/703/note", new { note = "   " })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await parent.GetFromJsonAsync<JsonElement>("/api/v1/titles/movie/703/note")).GetProperty("note").ValueKind);
        await parent.PutAsJsonAsync("/api/v1/titles/show/703/note", new { note = NoteText });
        Assert.Equal(JsonValueKind.Null, (await parent.GetFromJsonAsync<JsonElement>("/api/v1/titles/movie/703/note")).GetProperty("note").ValueKind);

        Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync("/api/v1/titles/movie/703/note")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PutAsJsonAsync("/api/v1/titles/movie/703/note", new { note = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/v1/kid/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/v1/titles/movie/703/note")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.PutAsJsonAsync("/api/v1/titles/movie/703/note", new { note = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/titles/movie/703/note")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PutAsJsonAsync("/api/v1/titles/movie/703/note", new { note = "nope" })).StatusCode);

        await parent.PutAsJsonAsync("/api/v1/titles/movie/703/note", new { note = NoteText });
        string title = await (await kid.GetAsync("/api/v1/kid/titles/movie/703")).Content.ReadAsStringAsync();
        string requests = await (await kid.GetAsync("/api/v1/kid/requests")).Content.ReadAsStringAsync();
        string wishlist = await (await kid.GetAsync("/api/v1/kid/wishlist")).Content.ReadAsStringAsync();
        string search = await (await kid.GetAsync("/api/v1/kid/search?q=Movie")).Content.ReadAsStringAsync();
        foreach (string body in new[] { title, requests, wishlist, search })
        {
            Assert.DoesNotContain(NoteText, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TopicName, body, StringComparison.Ordinal);
            Assert.DoesNotContain("doesthedogdie", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Topic_and_descriptor_blocks_fail_closed_when_the_source_does_not_load()
    {
        using KidRequestApiFactory factory = new();
        PrepareMovie(factory, 700);
        PrepareMovie(factory, 701);
        PrepareMovie(factory, 702);
        (HttpClient kid, int profileId) = await factory.SignInKidAsync(movieAgeLimit: 13, blockedTopics: ["zebratopic"]);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage unset = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unset.StatusCode);
        Assert.Equal("That one isn't available.", (await unset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.DoesNotContain("zebratopic", await unset.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(factory.Dtdd.Requests);

        await SaveKeyAsync(parent, FakeDtddServer.ValidApiKey);
        factory.Dtdd.Enqueue("/api/v3/items", 401, body: """{"error":"invalid_api_key","message":"hidden"}""");
        HttpResponseMessage failed = await KidRequestApiFactory.RequestAsync(kid, "movie", 700);
        string failedBody = await failed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.DoesNotContain("zebratopic", failedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden", failedBody, StringComparison.Ordinal);

        factory.Dtdd.SetList(700, "[]");
        Assert.Equal(HttpStatusCode.Created, (await KidRequestApiFactory.RequestAsync(kid, "movie", 700)).StatusCode);

        factory.Dtdd.Enqueue("/api/v3/items", 500, body: """{"error":"server","message":"hidden"}""");
        JsonElement unavailable = await parent.PostAsJsonAsync($"/api/v1/kid-profiles/{profileId}/preview", new { kind = "movie", tmdbId = 701 }).ResultBody();
        Assert.Equal("metadataUnavailable", unavailable.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("zebratopic", unavailable.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);

        (HttpClient descriptorKid, int descriptorProfile) = await factory.SignInKidAsync("Dee", movieAgeLimit: 13, blockedDescriptors: ["violence"]);
        HttpResponseMessage descriptor = await KidRequestApiFactory.RequestAsync(descriptorKid, "movie", 702);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, descriptor.StatusCode);
        Assert.DoesNotContain("iolence", await descriptor.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        JsonElement descriptorPreview = await parent.PostAsJsonAsync($"/api/v1/kid-profiles/{descriptorProfile}/preview", new { kind = "movie", tmdbId = 702 }).ResultBody();
        Assert.Equal("blockedDescriptor", descriptorPreview.GetProperty("reasonCode").GetString());
        Assert.Contains("Violence", descriptorPreview.GetProperty("reason").GetString(), StringComparison.Ordinal);

        factory.Tmdb.Enqueue("/3/movie/702/release_dates", 500);
        (HttpClient missed, _) = await factory.SignInKidAsync("Eve", movieAgeLimit: 13, blockedDescriptors: ["violence"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await KidRequestApiFactory.RequestAsync(missed, "movie", 702)).StatusCode);
    }

    [Fact]
    public async Task Kid_search_does_not_call_for_warnings_unless_the_profile_blocks_a_topic()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient kid, _) = await factory.SignInKidAsync(movieAgeLimit: 16);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        await SaveKeyAsync(parent, FakeDtddServer.ValidApiKey);

        Assert.Equal(HttpStatusCode.OK, (await kid.GetAsync("/api/v1/kid/search?q=Movie")).StatusCode);
        Assert.Empty(factory.Dtdd.Requests);

        (HttpClient blocked, _) = await factory.SignInKidAsync("Bea", movieAgeLimit: 16, blockedTopics: ["dogs"]);
        factory.Dtdd.SetList(603, MovieList(15, 603));
        factory.Dtdd.SetDetail(15, Topics(("dogs", 4, 0)));
        Assert.Equal(HttpStatusCode.OK, (await blocked.GetAsync("/api/v1/kid/search?q=Matrix")).StatusCode);
        Assert.NotEmpty(factory.Dtdd.Requests);
    }

    [Fact]
    public async Task An_explicit_allow_does_not_fetch_warnings_for_the_kid_decision()
    {
        using KidRequestApiFactory factory = new();
        PrepareMovie(factory, 700);
        (HttpClient kid, int profileId) = await factory.SignInKidAsync(movieAgeLimit: 5, blockedTopics: ["dogs"]);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        await SaveKeyAsync(parent, FakeDtddServer.ValidApiKey);
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsJsonAsync($"/api/v1/kid-profiles/{profileId}/titles", new { kind = "movie", tmdbId = 700, title = "Movie 700", blocked = false })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await KidRequestApiFactory.RequestAsync(kid, "movie", 700)).StatusCode);
        Assert.Empty(factory.Dtdd.Requests);
    }

    [Fact]
    public async Task The_warning_key_is_encrypted_and_never_returned()
    {
        using KidRequestApiFactory factory = new();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        const string secret = "fixture-dtdd-key";

        Assert.Equal(HttpStatusCode.NoContent, (await parent.PutAsJsonAsync($"/api/v1/settings/{DtddOptions.ApiKeySetting}", new { value = secret, isSecret = false })).StatusCode);
        string listed = await parent.GetStringAsync("/api/v1/settings");
        Assert.DoesNotContain(secret, listed, StringComparison.Ordinal);

        using IServiceScope scope = factory.Services.CreateScope();
        Setting stored = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Settings.AsNoTracking().SingleAsync(setting => setting.Key == DtddOptions.ApiKeySetting);
        Assert.True(stored.IsSecret);
        Assert.DoesNotContain(secret, stored.Value, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await parent.PutAsJsonAsync($"/api/v1/settings/{DtddOptions.ApiKeySetting}", new { value = "" })).StatusCode);
        Assert.False(await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Settings.AnyAsync(setting => setting.Key == DtddOptions.ApiKeySetting));
    }

    [Fact]
    public async Task Advisory_lists_reject_too_many_too_long_and_control_characters_and_fold_case()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        string[] fifty = [.. Enumerable.Range(0, 50).Select(index => $"topic-{index}")];

        HttpResponseMessage accepted = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new
        {
            name = "Ada",
            avatar = "fox",
            blockedDescriptors = new[] { " Violence ", "violence", "Language" },
            blockedTopics = fifty,
        });
        HttpResponseMessage tooMany = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new
        {
            name = "Bea",
            avatar = "fox",
            blockedTopics = Enumerable.Range(0, 51).Select(index => $"topic-{index}"),
        });
        HttpResponseMessage tooLong = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new
        {
            name = "Cy",
            avatar = "fox",
            blockedDescriptors = new[] { new string('a', 101) },
        });
        HttpResponseMessage control = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new
        {
            name = "Dee",
            avatar = "fox",
            blockedTopics = new[] { "bad\ntopic" },
        });
        JsonElement profile = await accepted.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(50, KidProfiles.MaxBlockedDescriptors);
        Assert.Equal(50, KidProfiles.MaxBlockedTopics);
        Assert.Equal(100, KidProfiles.MaxAdvisoryLength);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(["Violence", "Language"], Texts(profile.GetProperty("blockedDescriptors")));
        Assert.Equal(50, profile.GetProperty("blockedTopics").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, control.StatusCode);
    }

    private static void PrepareMovie(KidRequestApiFactory factory, int id)
    {
        factory.Tmdb.SetBody($"/3/movie/{id}", $$"""{"id":{{id}},"title":"Movie {{id}}","overview":"A story.","genres":[{"id":28,"name":"Action"}],"original_language":"en","vote_average":7}""");
        factory.Tmdb.SetBody($"/3/movie/{id}/release_dates", """
            {"results":[{"iso_3166_1":"US","release_dates":[{"certification":"PG","descriptors":["Violence"],"type":3}]}]}
            """);
        factory.Tmdb.SetBody($"/3/movie/{id}/watch/providers", """{"results":{}}""");
    }

    private static string MovieList(int itemId, int tmdbId) =>
        $$"""[{"id":{{itemId}},"itemTypeId":15,"itemTypeName":"Movie","tmdbId":{{tmdbId}}},{"id":{{itemId + 100}},"itemTypeId":16,"itemTypeName":"TV","tmdbId":{{tmdbId}}}]""";

    private static string Topics(params (string Name, int Yes, int No)[] stats)
    {
        string rows = string.Join(',', stats.Select((stat, index) =>
            $$"""{"topicId":{{index + 1}},"topicName":"{{stat.Name}}","yesSum":{{stat.Yes}},"noSum":{{stat.No}},"numComments":4,"itemId":15}"""));
        return $$"""{"id":15,"topicItemStats":[{{rows}}]}""";
    }

    private static void AssertTopics(JsonElement item, string? Note)
    {
        Assert.Equal([TopicName, "second warning"], Texts(item.GetProperty("contentWarnings")));
        Assert.Equal(ContentWarningCredit.Phrase, item.GetProperty("attribution").GetProperty("text").GetString());
        Assert.Equal(ContentWarningCredit.Link, item.GetProperty("attribution").GetProperty("url").GetString());
        Assert.Equal(["Violence"], Texts(item.GetProperty("descriptors")));
        Assert.DoesNotContain("yesSum", item.GetProperty("contentWarnings").ToString(), StringComparison.Ordinal);
        if (Note is null)
        {
            Assert.Equal(JsonValueKind.Null, item.GetProperty("parentNote").ValueKind);
        }
    }

    private static async Task SaveKeyAsync(HttpClient parent, string key)
    {
        HttpResponseMessage response = await parent.PutAsJsonAsync($"/api/v1/settings/{DtddOptions.ApiKeySetting}", new { value = key, isSecret = false });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<int> AskAsync(HttpClient kid, int tmdbId)
    {
        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", tmdbId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> QueueAsync(HttpClient parent)
    {
        HttpResponseMessage response = await parent.GetAsync(QueuePath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static IReadOnlyList<string> Texts(JsonElement array) =>
        [.. array.EnumerateArray().Select(item => item.GetString()!)];
}
