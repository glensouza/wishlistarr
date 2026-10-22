using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Kids;
using Wishlistarr.Data;

namespace Wishlistarr.Tests;

public sealed class KidProfileTests
{
    [Fact]
    public async Task Profiles_can_be_created_edited_and_listed()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        HttpResponseMessage created = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new
        {
            name = " Ada ",
            avatar = "fox",
            pin = "4821",
            movieAgeLimit = 7,
            tvAgeLimit = 10,
            plexUserId = "plex-1",
            weeklyAllowance = 3,
        });
        JsonElement profile = await created.Content.ReadFromJsonAsync<JsonElement>();
        int id = profile.GetProperty("id").GetInt32();
        HttpResponseMessage edited = await parent.PutAsJsonAsync($"/api/v1/kid-profiles/{id}", new
        {
            name = "Ada L",
            avatar = "owl",
            movieAgeLimit = 9,
            tvAgeLimit = 12,
            plexLabel = "ada-kids",
            weeklyAllowance = 5,
        });
        JsonElement updated = await edited.Content.ReadFromJsonAsync<JsonElement>();
        List<JsonElement> list = await parent.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid-profiles") ?? [];

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Ada", profile.GetProperty("name").GetString());
        Assert.Equal("plex-1", profile.GetProperty("plexUserId").GetString());
        Assert.True(profile.GetProperty("hasPin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("Ada L", updated.GetProperty("name").GetString());
        Assert.Equal("ada-kids", updated.GetProperty("plexLabel").GetString());
        Assert.Equal(9, updated.GetProperty("movieAgeLimit").GetInt32());
        Assert.Equal(12, updated.GetProperty("tvAgeLimit").GetInt32());
        Assert.Equal(5, updated.GetProperty("weeklyAllowance").GetInt32());
        Assert.True(updated.GetProperty("hasPin").GetBoolean());
        JsonElement listed = Assert.Single(list);
        Assert.Equal("owl", listed.GetProperty("avatar").GetString());
        Assert.False(listed.TryGetProperty("pinHash", out _));
        Assert.False(listed.TryGetProperty("pin", out _));
        Assert.Empty(Numbers(listed, "blockedGenreIds"));
        Assert.Empty(Numbers(listed, "blockedKeywordIds"));
        Assert.Empty(Texts(listed, "allowedOriginalLanguages"));
    }

    [Fact]
    public async Task Content_rules_are_stored_separately_on_each_profile()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        HttpResponseMessage created = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new
        {
            name = "Ada",
            avatar = "fox",
            blockedGenreIds = new int[] { 28, 28, 12 },
            blockedKeywordIds = new int[] { 1721 },
            allowedOriginalLanguages = new string[] { "EN", "ja" },
        });
        JsonElement profile = await created.Content.ReadFromJsonAsync<JsonElement>();
        int id = profile.GetProperty("id").GetInt32();
        HttpResponseMessage other = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new
        {
            name = "Ben",
            avatar = "fox",
            blockedGenreIds = new[] { 16 },
        });
        HttpResponseMessage zero = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Cy", avatar = "fox", blockedGenreIds = new[] { 0 } });
        HttpResponseMessage word = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Cy", avatar = "fox", allowedOriginalLanguages = new[] { "english" } });
        HttpResponseMessage tooMany = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Cy", avatar = "fox", blockedGenreIds = Enumerable.Range(1, KidProfiles.MaxBlockedGenres + 1) });
        HttpResponseMessage edited = await parent.PutAsJsonAsync($"/api/v1/kid-profiles/{id}", new
        {
            name = "Ada",
            avatar = "fox",
            blockedKeywordIds = new[] { 9 },
        });
        List<JsonElement> list = await parent.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid-profiles") ?? [];

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal([28, 12], Numbers(profile, "blockedGenreIds"));
        Assert.Equal([1721], Numbers(profile, "blockedKeywordIds"));
        Assert.Equal(["en", "ja"], Texts(profile, "allowedOriginalLanguages"));
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Contains("positive TMDB ids", await zero.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, word.StatusCode);
        Assert.Contains("ISO 639-1", await word.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("limited to 40", await tooMany.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        JsonElement updated = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Empty(Numbers(updated, "blockedGenreIds"));
        Assert.Equal([9], Numbers(updated, "blockedKeywordIds"));
        Assert.Empty(Texts(updated, "allowedOriginalLanguages"));
        JsonElement ada = Assert.Single(list, item => item.GetProperty("name").GetString() == "Ada");
        JsonElement ben = Assert.Single(list, item => item.GetProperty("name").GetString() == "Ben");
        Assert.Equal([9], Numbers(ada, "blockedKeywordIds"));
        Assert.Equal([16], Numbers(ben, "blockedGenreIds"));
        Assert.Empty(Numbers(ben, "blockedKeywordIds"));
        Assert.Empty(Texts(ben, "allowedOriginalLanguages"));
    }

    [Fact]
    public async Task Plex_label_defaults_to_the_profile_name()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        JsonElement named = await factory.CreateProfileAsync(parent, "Ben");
        HttpResponseMessage blank = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Cy", avatar = "fox", plexLabel = "  " });
        JsonElement blankProfile = await blank.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Ben", named.GetProperty("plexLabel").GetString());
        Assert.Equal("Cy", blankProfile.GetProperty("plexLabel").GetString());
    }

    [Theory]
    [InlineData("a,b")]
    [InlineData("a|b")]
    [InlineData("tab\there")]
    public async Task Invalid_plex_labels_are_rejected(string label)
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        HttpResponseMessage response = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Cy", avatar = "fox", plexLabel = label });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_default_label_is_validated_too()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        HttpResponseMessage comma = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new { name = "A, B", avatar = "fox" });
        HttpResponseMessage tooLong = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "x", avatar = "fox", plexLabel = new string('x', 65) });

        Assert.Equal(HttpStatusCode.BadRequest, comma.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Names_and_labels_must_be_unique_among_active_profiles()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        await factory.CreateProfileAsync(parent, "Dee", plexLabel: "shared");

        HttpResponseMessage sameName = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new { name = "dee", avatar = "fox" });
        HttpResponseMessage sameLabel = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Eve", avatar = "fox", plexLabel = "SHARED" });

        Assert.Equal(HttpStatusCode.Conflict, sameName.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sameLabel.StatusCode);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("12a4")]
    public async Task Malformed_pins_are_rejected(string pin)
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        HttpResponseMessage response = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Fay", avatar = "fox", pin });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Out_of_range_values_are_rejected()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        HttpResponseMessage age = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Gus", avatar = "fox", movieAgeLimit = 99 });
        HttpResponseMessage allowance = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Gus", avatar = "fox", weeklyAllowance = -1 });
        HttpResponseMessage avatar = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new { name = "Gus", avatar = "Not Valid" });
        HttpResponseMessage plexUser = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Gus", avatar = "fox", plexUserId = new string('1', 101) });

        Assert.Equal(HttpStatusCode.BadRequest, age.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, allowance.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, avatar.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, plexUser.StatusCode);
    }

    [Fact]
    public async Task Pin_is_stored_hashed()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        JsonElement profile = await factory.CreateProfileAsync(parent, "Hal", pin: "90817");

        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        KidProfile stored = await db.KidProfiles.AsNoTracking().SingleAsync();
        IPasswordHasher<KidProfile> hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<KidProfile>>();

        Assert.NotNull(stored.PinHash);
        Assert.DoesNotContain("90817", stored.PinHash);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(stored, stored.PinHash, "90817"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(stored, stored.PinHash, "90818"));
        Assert.DoesNotContain("90817", profile.GetRawText());
    }

    [Fact]
    public async Task Pin_is_required_once_profiles_have_different_limits()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        JsonElement first = await factory.CreateProfileAsync(parent, "Ida", movieAgeLimit: 7, tvAgeLimit: 7);
        await factory.CreateProfileAsync(parent, "Jon", movieAgeLimit: 7, tvAgeLimit: 7);

        HttpResponseMessage differentNoPin = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Kai", avatar = "fox", movieAgeLimit = 13, tvAgeLimit = 13 });
        HttpResponseMessage raise = await parent.PutAsJsonAsync(
            $"/api/v1/kid-profiles/{first.GetProperty("id").GetInt32()}",
            new { name = "Ida", avatar = "fox", movieAgeLimit = 12, tvAgeLimit = 12 });

        Assert.Equal(HttpStatusCode.BadRequest, differentNoPin.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, raise.StatusCode);
        string title = (await raise.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;
        Assert.Contains("Ida", title);
        Assert.Contains("Jon", title);
    }

    [Fact]
    public async Task Profiles_with_different_limits_need_a_pin_on_every_profile()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        await factory.CreateProfileAsync(parent, "Lia", pin: "1111", movieAgeLimit: 7);

        HttpResponseMessage second = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Max", avatar = "fox", movieAgeLimit = 13, pin = "2222" });
        int secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        HttpResponseMessage removePin = await parent.PutAsJsonAsync(
            $"/api/v1/kid-profiles/{secondId}",
            new { name = "Max", avatar = "fox", movieAgeLimit = 13, removePin = true });

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, removePin.StatusCode);
    }

    [Fact]
    public async Task Updating_without_a_pin_keeps_the_existing_pin()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        JsonElement profile = await factory.CreateProfileAsync(parent, "Nia", pin: "5555");
        int id = profile.GetProperty("id").GetInt32();

        HttpResponseMessage update = await parent.PutAsJsonAsync($"/api/v1/kid-profiles/{id}", new { name = "Nia B", avatar = "fox" });
        HttpResponseMessage wrong = await KidApiFactory.SignInAsync(await factory.CreateDeviceAsync(), id, "5556");
        HttpResponseMessage signIn = await KidApiFactory.SignInAsync(await factory.CreateDeviceAsync(), id, "5555");

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    }

    [Fact]
    public async Task Deleting_archives_the_profile_and_records_the_label_removal_flag()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        JsonElement profile = await factory.CreateProfileAsync(parent, "Oli", pin: "3333", plexLabel: "oli-label");
        int id = profile.GetProperty("id").GetInt32();
        HttpClient kid = await factory.CreateDeviceAsync();
        Assert.Equal(HttpStatusCode.OK, (await KidApiFactory.SignInAsync(kid, id, "3333")).StatusCode);

        HttpResponseMessage delete = await parent.DeleteAsync($"/api/v1/kid-profiles/{id}?removeOwnedLabels=true");
        HttpResponseMessage again = await parent.DeleteAsync($"/api/v1/kid-profiles/{id}");
        List<JsonElement> list = await parent.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid-profiles") ?? [];
        HttpResponseMessage kidAfter = await kid.GetAsync("/api/v1/kid/me");
        HttpResponseMessage signIn = await KidApiFactory.SignInAsync(await factory.CreateDeviceAsync(), id, "3333");
        HttpResponseMessage reuse = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name = "Oli", avatar = "fox", plexLabel = "oli-label" });

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Empty(list);
        Assert.Equal(HttpStatusCode.Unauthorized, kidAfter.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.Created, reuse.StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        KidProfile archived = await db.KidProfiles.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.NotNull(archived.ArchivedAt);
        Assert.True(archived.RemoveOwnedLabelsOnArchive);
        Assert.Equal("oli-label", archived.PlexLabel);
        Assert.Null(archived.PinHash);
    }

    [Fact]
    public async Task Deleting_without_the_flag_does_not_request_label_removal()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        JsonElement profile = await factory.CreateProfileAsync(parent, "Pam");
        int id = profile.GetProperty("id").GetInt32();

        await parent.DeleteAsync($"/api/v1/kid-profiles/{id}");

        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        KidProfile archived = await db.KidProfiles.AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.NotNull(archived.ArchivedAt);
        Assert.False(archived.RemoveOwnedLabelsOnArchive);
    }

    [Fact]
    public async Task Unknown_profiles_return_not_found()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        HttpResponseMessage update = await parent.PutAsJsonAsync("/api/v1/kid-profiles/999", new { name = "Q", avatar = "fox" });
        HttpResponseMessage unlock = await parent.PostAsync("/api/v1/kid-profiles/999/unlock", null);

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unlock.StatusCode);
    }

    [Fact]
    public async Task Profile_routes_require_the_parent_role()
    {
        using KidApiFactory factory = new();
        await factory.EnsureParentAsync();
        HttpClient anonymous = factory.CreateClient();
        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");

        foreach ((HttpClient client, HttpStatusCode expected) in new[]
        {
            (anonymous, HttpStatusCode.Unauthorized),
            (kid, HttpStatusCode.Forbidden),
        })
        {
            Assert.Equal(expected, (await client.GetAsync("/api/v1/kid-profiles")).StatusCode);
            Assert.Equal(expected, (await client.PostAsJsonAsync("/api/v1/kid-profiles", new { name = "Z", avatar = "fox" })).StatusCode);
            Assert.Equal(expected, (await client.PutAsJsonAsync("/api/v1/kid-profiles/1", new { name = "Z", avatar = "fox" })).StatusCode);
            Assert.Equal(expected, (await client.DeleteAsync("/api/v1/kid-profiles/1")).StatusCode);
            Assert.Equal(expected, (await client.PostAsync("/api/v1/kid-profiles/1/unlock", null)).StatusCode);
        }
    }

    [Fact]
    public async Task Keyword_list_stops_at_one_hundred_and_language_list_at_fifty()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        HttpResponseMessage accepted = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new
        {
            name = "Ada",
            avatar = "fox",
            blockedKeywordIds = Enumerable.Range(1, 100),
            allowedOriginalLanguages = Enumerable.Range(0, 50).Select(LanguageCode),
        });
        HttpResponseMessage tooManyKeywords = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new
        {
            name = "Bea",
            avatar = "fox",
            blockedKeywordIds = Enumerable.Range(1, 101),
        });
        HttpResponseMessage tooManyLanguages = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new
        {
            name = "Cy",
            avatar = "fox",
            allowedOriginalLanguages = Enumerable.Range(0, 51).Select(LanguageCode),
        });
        JsonElement profile = await accepted.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(100, KidProfiles.MaxBlockedKeywords);
        Assert.Equal(50, KidProfiles.MaxAllowedLanguages);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(100, Numbers(profile, "blockedKeywordIds").Count);
        Assert.Equal(50, Texts(profile, "allowedOriginalLanguages").Count);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyKeywords.StatusCode);
        Assert.Contains("limited to 100", await tooManyKeywords.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyLanguages.StatusCode);
        Assert.Contains("limited to 50", await tooManyLanguages.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static string LanguageCode(int index) => $"{(char)('a' + (index / 26))}{(char)('a' + (index % 26))}";

    private static List<int> Numbers(JsonElement profile, string name) =>
        [.. profile.GetProperty(name).EnumerateArray().Select(item => item.GetInt32())];

    private static List<string> Texts(JsonElement profile, string name) =>
        [.. profile.GetProperty(name).EnumerateArray().Select(item => item.GetString()!)];
}
