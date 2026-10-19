using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wishlistarr.Api.Kids;
using Wishlistarr.Api.Security;
using Wishlistarr.Core.Kids;
using Wishlistarr.Data;

namespace Wishlistarr.Tests;

public sealed class KidSignInTests
{
    private const string Pin = "90817";
    private const string WrongPin = "11223";

    [Fact]
    public async Task Correct_pin_starts_a_kid_session_that_cannot_reach_parent_routes()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ann", pin: Pin)).GetProperty("id").GetInt32();
        HttpClient device = factory.CreateDevice();

        HttpResponseMessage before = await device.GetAsync("/api/v1/kid/me");
        HttpResponseMessage signIn = await KidApiFactory.SignInAsync(device, id, Pin);
        HttpResponseMessage me = await device.GetAsync("/api/v1/kid/me");
        HttpResponseMessage parentRoute = await device.GetAsync("/api/v1/kid-profiles");
        HttpResponseMessage status = await device.GetAsync("/api/v1/auth/status");

        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        string cookie = Assert.Single(signIn.Headers.GetValues("Set-Cookie"), value => value.StartsWith(SecuritySetup.KidCookieName + "="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("Ann", (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, parentRoute.StatusCode);
        Assert.Equal(Principals.Kid, (await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("principalType").GetString());
    }

    [Fact]
    public async Task Signing_out_ends_the_kid_session()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Bo", pin: Pin)).GetProperty("id").GetInt32();
        HttpClient device = factory.CreateDevice();
        await KidApiFactory.SignInAsync(device, id, Pin);
        await AuthApiFactory.AddAntiforgeryTokenAsync(device);

        HttpResponseMessage signOut = await device.PostAsync("/api/v1/kid/signout", null);
        HttpResponseMessage after = await device.GetAsync("/api/v1/kid/me");

        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().KidSessions.AnyAsync());
    }

    [Fact]
    public async Task Kid_sessions_expire()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Cat", pin: Pin)).GetProperty("id").GetInt32();
        HttpClient device = factory.CreateDevice();
        await KidApiFactory.SignInAsync(device, id, Pin);

        factory.Clock.Advance(TimeSpan.FromDays(8));

        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/v1/kid/me")).StatusCode);
    }

    [Fact]
    public async Task Profiles_without_a_pin_sign_in_without_one()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Di")).GetProperty("id").GetInt32();

        HttpResponseMessage response = await KidApiFactory.SignInAsync(factory.CreateDevice(), id, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Pin_is_required_for_profiles_that_have_one()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ed", pin: Pin)).GetProperty("id").GetInt32();
        HttpClient device = factory.CreateDevice();

        HttpResponseMessage missing = await KidApiFactory.SignInAsync(device, id, null);
        HttpResponseMessage malformed = await KidApiFactory.SignInAsync(device, id, "12");
        HttpResponseMessage noProfile = await device.PostAsJsonAsync("/api/v1/kid/signin", new { pin = Pin });

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noProfile.StatusCode);
    }

    [Fact]
    public async Task Failed_pins_back_off_exponentially_on_the_same_device()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Flo", pin: Pin)).GetProperty("id").GetInt32();
        HttpClient device = factory.CreateDevice();

        List<int> waits = [];
        for (int attempt = 0; attempt < 5; attempt++)
        {
            HttpResponseMessage failed = await KidApiFactory.SignInAsync(device, id, WrongPin);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            int wait = await KidApiFactory.GetBackoffSecondsAsync(device, id);
            waits.Add(wait);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await KidApiFactory.SignInAsync(device, id, Pin)).StatusCode);

            factory.Clock.Advance(TimeSpan.FromSeconds(wait - 1));
            Assert.Equal(HttpStatusCode.TooManyRequests, (await KidApiFactory.SignInAsync(device, id, Pin)).StatusCode);
            factory.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal([2, 4, 8, 16, 32], waits);
        Assert.Equal(HttpStatusCode.OK, (await KidApiFactory.SignInAsync(device, id, Pin)).StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().KidPinAttempts.AnyAsync());
    }

    [Fact]
    public async Task Backoff_is_tracked_per_device()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Gil", pin: Pin)).GetProperty("id").GetInt32();
        int other = (await factory.CreateProfileAsync(parent, "Hu", pin: "2468")).GetProperty("id").GetInt32();
        HttpClient first = factory.CreateDevice();
        HttpClient second = factory.CreateDevice();

        await KidApiFactory.SignInAsync(first, id, WrongPin);
        HttpResponseMessage firstBlocked = await KidApiFactory.SignInAsync(first, id, Pin);
        HttpResponseMessage firstOtherProfile = await KidApiFactory.SignInAsync(first, other, "2468");
        HttpResponseMessage secondOk = await KidApiFactory.SignInAsync(second, id, Pin);

        Assert.Equal(HttpStatusCode.TooManyRequests, firstBlocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstOtherProfile.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondOk.StatusCode);
    }

    [Fact]
    public void Backoff_is_capped()
    {
        KidOptions options = new();

        Assert.Equal(TimeSpan.FromSeconds(2), options.GetBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(4), options.GetBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(512), options.GetBackoff(9));
        Assert.Equal(options.BackoffMax, options.GetBackoff(10));
        Assert.Equal(options.BackoffMax, options.GetBackoff(10_000));
    }

    [Fact]
    public async Task Failures_are_forgotten_after_the_reset_window()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ivy", pin: Pin)).GetProperty("id").GetInt32();
        HttpClient device = factory.CreateDevice();
        await factory.FailAndWaitAsync(device, id, WrongPin);
        await factory.FailAndWaitAsync(device, id, WrongPin);

        factory.Clock.Advance(TimeSpan.FromHours(2));
        HttpResponseMessage failed = await KidApiFactory.SignInAsync(device, id, WrongPin);

        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        Assert.Equal(2, await KidApiFactory.GetBackoffSecondsAsync(device, id));
    }

    [Fact]
    public async Task Parent_is_notified_once_when_the_threshold_is_reached()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Jay", pin: Pin)).GetProperty("id").GetInt32();
        HttpClient device = factory.CreateDevice();

        for (int attempt = 0; attempt < 4; attempt++)
        {
            await factory.FailAndWaitAsync(device, id, WrongPin);
        }

        Assert.Empty(factory.Notifier.Sent);

        await factory.FailAndWaitAsync(device, id, WrongPin);
        await factory.FailAndWaitAsync(device, id, WrongPin);

        ParentNotification notification = Assert.Single(factory.Notifier.Sent);
        Assert.Equal(ParentNotificationKinds.PinFailures, notification.Kind);
        Assert.Contains("Jay", notification.Message);
        Assert.DoesNotContain(WrongPin, notification.Message);
        Assert.DoesNotContain(Pin, notification.Message);
    }

    [Fact]
    public async Task Profile_locks_after_repeated_failures_across_devices_until_a_parent_unlocks_it()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Kim", pin: Pin)).GetProperty("id").GetInt32();
        HttpClient first = factory.CreateDevice();
        HttpClient second = factory.CreateDevice();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            await factory.FailAndWaitAsync(first, id, WrongPin);
            await factory.FailAndWaitAsync(second, id, WrongPin);
        }

        HttpResponseMessage rightPinWhileLocked = await KidApiFactory.SignInAsync(factory.CreateDevice(), id, Pin);
        List<JsonElement> listed = await parent.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid-profiles") ?? [];
        HttpResponseMessage unlock = await parent.PostAsync($"/api/v1/kid-profiles/{id}/unlock", null);
        HttpResponseMessage afterUnlock = await KidApiFactory.SignInAsync(first, id, Pin);

        Assert.Equal(HttpStatusCode.Locked, rightPinWhileLocked.StatusCode);
        Assert.True(Assert.Single(listed).GetProperty("isLocked").GetBoolean());
        Assert.Equal(
            [ParentNotificationKinds.PinFailures, ParentNotificationKinds.ProfileLocked],
            factory.Notifier.Sent.Select(sent => sent.Kind));
        Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterUnlock.StatusCode);
        List<JsonElement> relisted = await parent.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid-profiles") ?? [];
        Assert.False(Assert.Single(relisted).GetProperty("isLocked").GetBoolean());
        using IServiceScope scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().KidPinAttempts.AnyAsync(attempt => attempt.FailedCount > 0));
    }

    [Fact]
    public async Task Locking_ends_existing_kid_sessions()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Leo", pin: Pin)).GetProperty("id").GetInt32();
        HttpClient signedIn = factory.CreateDevice();
        await KidApiFactory.SignInAsync(signedIn, id, Pin);
        HttpClient attacker = factory.CreateDevice();

        for (int attempt = 0; attempt < 10; attempt++)
        {
            await factory.FailAndWaitAsync(attacker, id, WrongPin);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await signedIn.GetAsync("/api/v1/kid/me")).StatusCode);
    }

    [Fact]
    public async Task Unknown_archived_and_wrong_pin_attempts_look_the_same()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int live = (await factory.CreateProfileAsync(parent, "Mia", pin: Pin)).GetProperty("id").GetInt32();
        int archived = (await factory.CreateProfileAsync(parent, "Ned", pin: "2468")).GetProperty("id").GetInt32();
        await parent.DeleteAsync($"/api/v1/kid-profiles/{archived}");

        HttpResponseMessage wrong = await KidApiFactory.SignInAsync(factory.CreateDevice(), live, WrongPin);
        HttpResponseMessage unknown = await KidApiFactory.SignInAsync(factory.CreateDevice(), 12345, WrongPin);
        HttpResponseMessage gone = await KidApiFactory.SignInAsync(factory.CreateDevice(), archived, "2468");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        string? wrongTitle = await TitleAsync(wrong);
        Assert.Equal(wrongTitle, await TitleAsync(unknown));
        Assert.Equal(wrongTitle, await TitleAsync(gone));
        Assert.Equal(wrong.Headers.Contains("Retry-After"), unknown.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Picker_lists_only_names_avatars_and_whether_a_pin_is_needed()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();
        await factory.CreateProfileAsync(parent, "Oz", pin: Pin, movieAgeLimit: 7, plexLabel: "secret-label");
        await factory.CreateProfileAsync(parent, "Pia", pin: "2468", movieAgeLimit: 12);
        int removed = (await factory.CreateProfileAsync(parent, "Quin", pin: "1357", movieAgeLimit: 12)).GetProperty("id").GetInt32();
        await parent.DeleteAsync($"/api/v1/kid-profiles/{removed}");

        List<JsonElement> picker = await factory.CreateClient().GetFromJsonAsync<List<JsonElement>>("/api/v1/kid/profiles") ?? [];

        Assert.Equal(["Oz", "Pia"], picker.Select(entry => entry.GetProperty("name").GetString()));
        foreach (JsonElement entry in picker)
        {
            Assert.Equal(
                ["avatar", "id", "name", "requiresPin"],
                entry.EnumerateObject().Select(property => property.Name).Order());
        }
    }

    [Fact]
    public async Task Sign_in_and_picker_are_the_only_anonymous_kid_routes()
    {
        using KidApiFactory factory = new();
        await factory.EnsureParentAsync();
        HttpClient anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1/kid/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/kid/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/kid/signout", null)).StatusCode);
    }

    [Fact]
    public async Task Parent_session_cannot_use_kid_routes()
    {
        using KidApiFactory factory = new();
        HttpClient parent = await factory.CreateSignedInClientAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await parent.GetAsync("/api/v1/kid/me")).StatusCode);
    }

    [Fact]
    public async Task Sign_in_is_rate_limited_per_address()
    {
        using KidApiFactory factory = new() { Settings = new Dictionary<string, string?> { ["Auth:LoginAttemptsPerMinute"] = "2" } };
        HttpClient parent = await factory.CreateSignedInClientAsync();
        int id = (await factory.CreateProfileAsync(parent, "Ray", pin: Pin)).GetProperty("id").GetInt32();
        HttpClient device = factory.CreateDevice();

        HttpStatusCode last = HttpStatusCode.OK;
        for (int attempt = 0; attempt < 6 && last != HttpStatusCode.TooManyRequests; attempt++)
        {
            last = (await KidApiFactory.SignInAsync(device, id, Pin)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task Logging_notifier_writes_the_message_without_extra_data()
    {
        List<string> lines = [];
        LoggingParentNotifier notifier = new(new CapturingLogger<LoggingParentNotifier>(lines));

        await notifier.NotifyAsync(new ParentNotification(ParentNotificationKinds.PinFailures, "Ada was typed wrong"));

        string line = Assert.Single(lines);
        Assert.Contains(ParentNotificationKinds.PinFailures, line);
        Assert.Contains("Ada was typed wrong", line);
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    private sealed class CapturingLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }
}
