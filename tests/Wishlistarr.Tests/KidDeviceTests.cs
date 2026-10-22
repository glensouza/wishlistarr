using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Security;
using Wishlistarr.Data;

namespace Wishlistarr.Tests;

public sealed class KidDeviceTests
{
    private const string Pin = "90817";
    private const string DevicesPath = "/api/v1/kid-devices";
    private const string PairPath = "/api/v1/kid/pair";

    [Fact]
    public async Task Unpaired_browsers_only_get_the_parent_sign_in()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        HttpClient browser = factory.CreateClient();

        HttpResponseMessage picker = await browser.GetAsync("/api/v1/kid/profiles");
        HttpResponseMessage signIn = await KidApiFactory.SignInAsync(browser, id, null);
        JsonElement status = await browser.GetFromJsonAsync<JsonElement>("/api/v1/auth/status");
        HttpResponseMessage parentPicker = await parent.GetAsync("/api/v1/kid/profiles");

        Assert.Equal(HttpStatusCode.Unauthorized, picker.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("kidDevice").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, parentPicker.StatusCode);
    }

    [Fact]
    public async Task Pairing_sets_a_long_lived_http_only_device_cookie_and_stores_only_hashes()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        (_, string code) = await CreateDeviceAsync(parent, "Living room", id);
        HttpClient browser = factory.CreateClient();

        HttpResponseMessage paired = await browser.PostAsJsonAsync(PairPath, new { code = code.ToLowerInvariant() });
        JsonElement status = await browser.GetFromJsonAsync<JsonElement>("/api/v1/auth/status");
        List<JsonElement> picker = await browser.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid/profiles") ?? [];
        string listed = await parent.GetStringAsync(DevicesPath);

        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        Assert.Equal("Living room", (await paired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        string cookie = Assert.Single(paired.Headers.GetValues("Set-Cookie"), value => value.StartsWith(KidDevices.CookieName + "="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        string expires = cookie.Split(';').Select(part => part.Trim()).Single(part => part.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))[8..];
        Assert.True(DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.AddDays(300));
        Assert.Equal("Living room", status.GetProperty("kidDevice").GetProperty("name").GetString());
        Assert.Equal(["Ada"], picker.Select(entry => entry.GetProperty("name").GetString()));

        string token = Uri.UnescapeDataString(cookie.Split(';')[0][(KidDevices.CookieName.Length + 1)..]);
        string secret = token[(token.IndexOf('.', StringComparison.Ordinal) + 1)..];
        Assert.DoesNotContain(secret, listed, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", listed, StringComparison.OrdinalIgnoreCase);
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        KidDevice device = await db.KidDevices.SingleAsync();
        Assert.NotNull(device.TokenHash);
        Assert.DoesNotContain(secret, device.TokenHash, StringComparison.Ordinal);
        Assert.False(await db.KidPairingCodes.AnyAsync());
    }

    [Fact]
    public async Task Pairing_codes_work_only_once()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        (_, string code) = await CreateDeviceAsync(parent, "Tablet", id);
        HttpClient first = factory.CreateClient();
        HttpClient second = factory.CreateClient();

        HttpResponseMessage used = await first.PostAsJsonAsync(PairPath, new { code });
        HttpResponseMessage reused = await second.PostAsJsonAsync(PairPath, new { code });

        Assert.Equal(HttpStatusCode.OK, used.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/kid/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/v1/kid/profiles")).StatusCode);
    }

    [Fact]
    public async Task Pairing_codes_expire()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        (_, string late) = await CreateDeviceAsync(parent, "Late", id);
        factory.Clock.Advance(TimeSpan.FromMinutes(9));
        (_, string onTime) = await CreateDeviceAsync(parent, "On time", id);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));

        HttpResponseMessage expired = await factory.CreateClient().PostAsJsonAsync(PairPath, new { code = late });
        HttpResponseMessage valid = await factory.CreateClient().PostAsJsonAsync(PairPath, new { code = onTime });

        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task A_new_code_replaces_the_previous_one()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        (int deviceId, string old) = await CreateDeviceAsync(parent, "Tablet", id);

        HttpResponseMessage reissued = await parent.PostAsync($"{DevicesPath}/{deviceId}/pairing-code", null);
        string code = (await reissued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        Assert.Equal(HttpStatusCode.OK, reissued.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().PostAsJsonAsync(PairPath, new { code = old })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync(PairPath, new { code })).StatusCode);
    }

    [Fact]
    public async Task Concurrent_redemptions_of_one_code_pair_only_one_browser()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();

        for (int round = 0; round < 5; round++)
        {
            (_, string code) = await CreateDeviceAsync(parent, $"Tablet {round}", id);
            HttpClient[] browsers = [.. Enumerable.Range(0, 8).Select(_ => factory.CreateClient())];

            HttpResponseMessage[] responses = await Task.WhenAll(browsers.Select(browser => browser.PostAsJsonAsync(PairPath, new { code })));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.All(
                responses.Where(response => response.StatusCode != HttpStatusCode.OK),
                response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
            int paired = 0;
            foreach (HttpClient browser in browsers)
            {
                paired += (await browser.GetAsync("/api/v1/kid/profiles")).StatusCode == HttpStatusCode.OK ? 1 : 0;
            }

            Assert.Equal(1, paired);
        }
    }

    [Fact]
    public async Task Each_device_shows_and_signs_in_only_its_assigned_profiles()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int ada = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        int bo = (await factory.CreateProfileAsync(parent, "Bo")).GetProperty("id").GetInt32();
        HttpClient adaTablet = await factory.PairDeviceAsync(parent, "Ada tablet", [ada]);
        HttpClient shared = await factory.PairDeviceAsync(parent, "Shared", [ada, bo]);

        List<JsonElement> adaPicker = await adaTablet.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid/profiles") ?? [];
        List<JsonElement> sharedPicker = await shared.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid/profiles") ?? [];
        HttpResponseMessage notAssigned = await KidApiFactory.SignInAsync(adaTablet, bo, null);
        HttpResponseMessage assigned = await KidApiFactory.SignInAsync(shared, bo, null);

        Assert.Equal(["Ada"], adaPicker.Select(entry => entry.GetProperty("name").GetString()));
        Assert.Equal(["Ada", "Bo"], sharedPicker.Select(entry => entry.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, notAssigned.StatusCode);
        Assert.Equal("Invalid PIN.", (await notAssigned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
    }

    [Fact]
    public async Task Unassigning_a_profile_updates_the_picker_and_ends_its_session_on_that_device()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int ada = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        int bo = (await factory.CreateProfileAsync(parent, "Bo")).GetProperty("id").GetInt32();
        HttpClient tablet = await factory.PairDeviceAsync(parent, "Tablet", [ada, bo]);
        Assert.Equal(HttpStatusCode.OK, (await KidApiFactory.SignInAsync(tablet, bo, null)).StatusCode);
        int deviceId = (await ListDevicesAsync(parent)).Single().GetProperty("id").GetInt32();

        HttpResponseMessage updated = await parent.PutAsJsonAsync($"{DevicesPath}/{deviceId}", new { name = "Ada's tablet", profileIds = new[] { ada } });
        List<JsonElement> picker = await tablet.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid/profiles") ?? [];
        HttpResponseMessage me = await tablet.GetAsync("/api/v1/kid/me");

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement device = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ada's tablet", device.GetProperty("name").GetString());
        Assert.Equal([ada], device.GetProperty("profileIds").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Equal(["Ada"], picker.Select(entry => entry.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Device_requests_are_validated()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int ada = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        int archived = (await factory.CreateProfileAsync(parent, "Bo")).GetProperty("id").GetInt32();
        await parent.DeleteAsync($"/api/v1/kid-profiles/{archived}");

        HttpResponseMessage noName = await parent.PostAsJsonAsync(DevicesPath, new { name = " ", profileIds = new[] { ada } });
        HttpResponseMessage longName = await parent.PostAsJsonAsync(DevicesPath, new { name = new string('a', 51), profileIds = new[] { ada } });
        HttpResponseMessage unknown = await parent.PostAsJsonAsync(DevicesPath, new { name = "Tablet", profileIds = new[] { 999 } });
        HttpResponseMessage gone = await parent.PostAsJsonAsync(DevicesPath, new { name = "Tablet", profileIds = new[] { archived } });
        HttpResponseMessage missing = await parent.PutAsJsonAsync($"{DevicesPath}/999", new { name = "Tablet", profileIds = new[] { ada } });
        HttpResponseMessage badCode = await factory.CreateClient().PostAsJsonAsync(PairPath, new { code = "not a code" });

        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, gone.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badCode.StatusCode);
        Assert.Empty(await ListDevicesAsync(parent));
    }

    [Fact]
    public async Task Revoking_a_device_stops_its_token_and_kid_sessions_immediately()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada", pin: Pin)).GetProperty("id").GetInt32();
        HttpClient revoked = await factory.PairDeviceAsync(parent, "Old tablet", [id]);
        HttpClient kept = await factory.PairDeviceAsync(parent, "New tablet", [id]);
        Assert.Equal(HttpStatusCode.OK, (await KidApiFactory.SignInAsync(revoked, id, Pin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await KidApiFactory.SignInAsync(kept, id, Pin)).StatusCode);
        await AuthApiFactory.AddAntiforgeryTokenAsync(revoked);
        Assert.Equal(HttpStatusCode.Unauthorized, (await KidApiFactory.SignInAsync(revoked, id, "11223")).StatusCode);
        int deviceId = (await ListDevicesAsync(parent)).Single(device => device.GetProperty("name").GetString() == "Old tablet").GetProperty("id").GetInt32();

        HttpResponseMessage revoke = await parent.DeleteAsync($"{DevicesPath}/{deviceId}");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.GetAsync("/api/v1/kid/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.GetAsync("/api/v1/kid/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await KidApiFactory.SignInAsync(revoked, id, Pin)).StatusCode);
        JsonElement status = await revoked.GetFromJsonAsync<JsonElement>("/api/v1/auth/status");
        Assert.Equal(JsonValueKind.Null, status.GetProperty("kidDevice").ValueKind);
        Assert.Null(status.GetProperty("principalType").GetString());
        Assert.Equal(HttpStatusCode.OK, (await kept.GetAsync("/api/v1/kid/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await parent.DeleteAsync($"{DevicesPath}/{deviceId}")).StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        Assert.Equal(1, await db.KidSessions.CountAsync());
        Assert.False(await db.KidPinAttempts.AnyAsync(attempt => attempt.KidDeviceId == deviceId));
    }

    [Fact]
    public async Task Re_pairing_a_device_replaces_its_token()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        HttpClient old = await factory.PairDeviceAsync(parent, "Tablet", [id]);
        Assert.Equal(HttpStatusCode.OK, (await KidApiFactory.SignInAsync(old, id, null)).StatusCode);
        int deviceId = (await ListDevicesAsync(parent)).Single().GetProperty("id").GetInt32();
        HttpResponseMessage reissued = await parent.PostAsync($"{DevicesPath}/{deviceId}/pairing-code", null);
        string code = (await reissued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        HttpClient replacement = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await old.GetAsync("/api/v1/kid/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await replacement.PostAsJsonAsync(PairPath, new { code })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/v1/kid/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/v1/kid/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await replacement.GetAsync("/api/v1/kid/profiles")).StatusCode);
    }

    [Fact]
    public async Task A_kid_session_only_works_on_the_device_it_started_on()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        HttpClient first = await factory.PairDeviceAsync(parent, "First", [id]);
        HttpResponseMessage signIn = await KidApiFactory.SignInAsync(first, id, null);
        string kidCookie = Assert.Single(signIn.Headers.GetValues("Set-Cookie"), value => value.StartsWith(SecuritySetup.KidCookieName + "=")).Split(';')[0];
        string secondDevice = await DeviceCookieAsync(factory, parent, "Second", id);
        HttpClient raw = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        HttpRequestMessage moved = new(HttpMethod.Get, "/api/v1/kid/me");
        moved.Headers.Add("Cookie", $"{kidCookie}; {secondDevice}");
        HttpRequestMessage withoutDevice = new(HttpMethod.Get, "/api/v1/kid/me");
        withoutDevice.Headers.Add("Cookie", kidCookie);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/v1/kid/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(moved)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(withoutDevice)).StatusCode);
    }

    [Fact]
    public async Task Leaving_kid_mode_needs_the_parent_password()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        HttpClient tablet = await factory.PairDeviceAsync(parent, "Tablet", [id]);
        Assert.Equal(HttpStatusCode.OK, (await KidApiFactory.SignInAsync(tablet, id, null)).StatusCode);
        await AuthApiFactory.AddAntiforgeryTokenAsync(tablet);
        HttpClient unpaired = factory.CreateClient();

        HttpResponseMessage fromUnpaired = await unpaired.PostAsJsonAsync("/api/v1/kid/exit", new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword });
        HttpResponseMessage wrong = await tablet.PostAsJsonAsync("/api/v1/kid/exit", new { username = ApiFactory.ParentUsername, password = "not the password" });
        HttpResponseMessage stillKid = await tablet.GetAsync("/api/v1/kid/me");
        HttpResponseMessage exit = await tablet.PostAsJsonAsync("/api/v1/kid/exit", new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword });
        JsonElement status = await tablet.GetFromJsonAsync<JsonElement>("/api/v1/auth/status");

        Assert.Equal(HttpStatusCode.Unauthorized, fromUnpaired.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillKid.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, exit.StatusCode);
        Assert.Equal(Principals.Parent, status.GetProperty("principalType").GetString());
        Assert.Equal("Tablet", status.GetProperty("kidDevice").GetProperty("name").GetString());
        using IServiceScope scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().KidSessions.AnyAsync());
    }

    [Fact]
    public async Task Leaving_kid_mode_needs_the_antiforgery_token_while_a_kid_is_signed_in()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        HttpClient tablet = await factory.PairDeviceAsync(parent, "Tablet", [id]);
        Assert.Equal(HttpStatusCode.OK, (await KidApiFactory.SignInAsync(tablet, id, null)).StatusCode);

        HttpResponseMessage exit = await tablet.PostAsJsonAsync("/api/v1/kid/exit", new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword });

        Assert.Equal(HttpStatusCode.BadRequest, exit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tablet.GetAsync("/api/v1/kid/me")).StatusCode);
    }

    [Fact]
    public async Task A_kid_signing_in_ends_the_parent_session_in_that_browser()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        HttpClient tablet = await factory.PairDeviceAsync(parent, "Tablet", [id]);
        await AuthApiFactory.AddAntiforgeryTokenAsync(tablet);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await tablet.PostAsJsonAsync("/api/v1/kid/exit", new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword })).StatusCode);
        await AuthApiFactory.AddAntiforgeryTokenAsync(tablet);

        HttpResponseMessage signIn = await KidApiFactory.SignInAsync(tablet, id, null);
        JsonElement status = await tablet.GetFromJsonAsync<JsonElement>("/api/v1/auth/status");

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal(Principals.Kid, status.GetProperty("principalType").GetString());
        Assert.Equal(HttpStatusCode.OK, (await tablet.GetAsync("/api/v1/kid/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tablet.GetAsync(DevicesPath)).StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().ParentSessions.CountAsync());
    }

    [Fact]
    public async Task Parent_device_list_marks_the_current_browser_and_pending_codes()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ada")).GetProperty("id").GetInt32();
        HttpClient tablet = await factory.PairDeviceAsync(parent, "Tablet", [id]);
        await CreateDeviceAsync(parent, "Pending", id);
        await AuthApiFactory.AddAntiforgeryTokenAsync(tablet);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await tablet.PostAsJsonAsync("/api/v1/kid/exit", new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword })).StatusCode);

        List<JsonElement> devices = await ListDevicesAsync(tablet);

        JsonElement current = devices.Single(device => device.GetProperty("name").GetString() == "Tablet");
        JsonElement pending = devices.Single(device => device.GetProperty("name").GetString() == "Pending");
        Assert.True(current.GetProperty("isCurrent").GetBoolean());
        Assert.True(current.GetProperty("isPaired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("pairingCodeExpiresAt").ValueKind);
        Assert.False(pending.GetProperty("isCurrent").GetBoolean());
        Assert.False(pending.GetProperty("isPaired").GetBoolean());
        Assert.Equal(JsonValueKind.String, pending.GetProperty("pairingCodeExpiresAt").ValueKind);
        Assert.DoesNotContain(devices.SelectMany(device => device.EnumerateObject()), property => property.Name is "code" or "tokenHash" or "codeHash");
    }

    [Fact]
    public void Pairing_codes_are_normalized()
    {
        Assert.Equal("ABCD0113", KidDevices.NormalizeCode("abcd-o1i3"));
        Assert.Equal("ABCD0123", KidDevices.NormalizeCode(" ABCD 0123 "));
        Assert.Null(KidDevices.NormalizeCode("ABCD-012"));
        Assert.Null(KidDevices.NormalizeCode("ABCD-012U"));
        Assert.Null(KidDevices.NormalizeCode(null));
    }

    private static async Task<(int Id, string Code)> CreateDeviceAsync(HttpClient parent, string name, params int[] profileIds)
    {
        HttpResponseMessage created = await parent.PostAsJsonAsync(DevicesPath, new { name, profileIds });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonElement body = await created.Content.ReadFromJsonAsync<JsonElement>();
        string code = body.GetProperty("code").GetString()!;
        Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}$", code);
        return (body.GetProperty("device").GetProperty("id").GetInt32(), code);
    }

    private static async Task<List<JsonElement>> ListDevicesAsync(HttpClient parent) =>
        await parent.GetFromJsonAsync<List<JsonElement>>(DevicesPath) ?? [];

    private static async Task<string> DeviceCookieAsync(KidApiFactory factory, HttpClient parent, string name, int profileId)
    {
        (_, string code) = await CreateDeviceAsync(parent, name, profileId);
        HttpResponseMessage paired = await factory.CreateClient().PostAsJsonAsync(PairPath, new { code });
        return Assert.Single(paired.Headers.GetValues("Set-Cookie"), value => value.StartsWith(KidDevices.CookieName + "=")).Split(';')[0];
    }
}
