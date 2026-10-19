using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Security;
using Wishlistarr.Data;

namespace Wishlistarr.Tests;

public sealed class AuthTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task First_run_requires_parent_account_creation()
    {
        using AuthApiFactory fresh = new();
        HttpClient client = fresh.CreateClient();
        HttpClient keyed = fresh.CreateClient();
        keyed.DefaultRequestHeaders.Add(ApiKeyService.HeaderName, await fresh.GetApiKeyAsync());

        JsonElement before = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status");
        HttpResponseMessage blocked = await keyed.GetAsync("/api/v1/system/status");
        HttpResponseMessage anonymous = await client.GetAsync("/api/v1/system/status");
        HttpResponseMessage loginBeforeSetup = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = "parent", password = ApiFactory.ParentPassword });
        HttpResponseMessage setup = await client.PostAsJsonAsync(
            "/api/v1/auth/setup",
            new { username = "parent", password = ApiFactory.ParentPassword });
        JsonElement after = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status");
        HttpResponseMessage unblocked = await keyed.GetAsync("/api/v1/system/status");
        HttpResponseMessage signedIn = await client.GetAsync("/api/v1/system/status");

        Assert.True(before.GetProperty("setupRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("principalType").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("application/problem+json", blocked.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, loginBeforeSetup.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, setup.StatusCode);
        Assert.False(after.GetProperty("setupRequired").GetBoolean());
        Assert.Equal(Principals.Parent, after.GetProperty("principalType").GetString());
        Assert.Equal("parent", after.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, unblocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    [Fact]
    public async Task Setup_can_only_run_once()
    {
        await factory.EnsureParentAsync();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/setup",
            new { username = "intruder", password = "another long password" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        Assert.Equal(1, await db.ParentAccounts.CountAsync());
    }

    [Fact]
    public async Task Setup_rejects_weak_or_missing_credentials()
    {
        using AuthApiFactory fresh = new();
        HttpClient client = fresh.CreateClient();

        HttpResponseMessage shortPassword = await client.PostAsJsonAsync(
            "/api/v1/auth/setup",
            new { username = "parent", password = "short" });
        HttpResponseMessage missing = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = "parent" });
        HttpResponseMessage blankName = await client.PostAsJsonAsync(
            "/api/v1/auth/setup",
            new { username = " ", password = ApiFactory.ParentPassword });

        foreach (HttpResponseMessage response in new[] { shortPassword, missing, blankName })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        JsonElement status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status");
        Assert.True(status.GetProperty("setupRequired").GetBoolean());
    }

    [Fact]
    public async Task Password_is_stored_as_a_hash()
    {
        await factory.EnsureParentAsync();

        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        ParentAccount account = await db.ParentAccounts.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(ApiFactory.ParentPassword, account.PasswordHash);
        Assert.True(account.PasswordHash.Length > 40);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_correct_password_starts_a_cookie_session()
    {
        await factory.EnsureParentAsync();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage wrong = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = ApiFactory.ParentUsername, password = "not the password" });
        HttpResponseMessage unknown = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = "nobody", password = ApiFactory.ParentPassword });
        HttpResponseMessage beforeLogin = await client.GetAsync("/api/v1/system/status");
        HttpResponseMessage login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = "PARENT", password = ApiFactory.ParentPassword });
        HttpResponseMessage afterLogin = await client.GetAsync("/api/v1/system/status");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, beforeLogin.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        string cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith(SecuritySetup.CookieName + "="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, afterLogin.StatusCode);
    }

    [Fact]
    public async Task Sessions_can_be_listed_and_revoked()
    {
        HttpClient first = await factory.CreateSignedInClientAsync();
        HttpClient second = await factory.CreateSignedInClientAsync();

        List<JsonElement> sessions = await first.GetFromJsonAsync<List<JsonElement>>("/api/v1/auth/sessions") ?? [];
        JsonElement current = Assert.Single(sessions, session => session.GetProperty("isCurrent").GetBoolean());
        string currentId = current.GetProperty("id").GetString()!;
        JsonElement secondSession = await GetCurrentSessionAsync(second);
        string secondId = secondSession.GetProperty("id").GetString()!;

        HttpResponseMessage revoke = await first.DeleteAsync($"/api/v1/auth/sessions/{secondId}");
        HttpResponseMessage revokedClient = await second.GetAsync("/api/v1/system/status");
        HttpResponseMessage missing = await first.DeleteAsync("/api/v1/auth/sessions/does-not-exist");
        List<JsonElement> remaining = await first.GetFromJsonAsync<List<JsonElement>>("/api/v1/auth/sessions") ?? [];

        Assert.True(sessions.Count >= 2);
        Assert.NotEqual(currentId, secondId);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedClient.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain(remaining, session => session.GetProperty("id").GetString() == secondId);
        Assert.Contains(remaining, session => session.GetProperty("id").GetString() == currentId);
    }

    [Fact]
    public async Task Sessions_of_other_accounts_are_not_listed_or_revocable()
    {
        using AuthApiFactory fresh = new();
        HttpClient client = await fresh.CreateSignedInClientAsync();
        using (IServiceScope scope = fresh.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            ParentAccount other = new() { Username = "other-" + Guid.NewGuid(), PasswordHash = "x" };
            other.Sessions.Add(new ParentSession
            {
                Id = "foreign-session",
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            });
            db.ParentAccounts.Add(other);
            await db.SaveChangesAsync();
        }

        List<JsonElement> sessions = await client.GetFromJsonAsync<List<JsonElement>>("/api/v1/auth/sessions") ?? [];
        HttpResponseMessage revoke = await client.DeleteAsync("/api/v1/auth/sessions/foreign-session");

        Assert.DoesNotContain(sessions, session => session.GetProperty("id").GetString() == "foreign-session");
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);
        using IServiceScope verify = fresh.Services.CreateScope();
        WishlistarrDbContext check = verify.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        Assert.True(await check.ParentSessions.AnyAsync(session => session.Id == "foreign-session"));
    }

    [Fact]
    public async Task Overlong_login_password_is_rejected()
    {
        await factory.EnsureParentAsync();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = ApiFactory.ParentUsername, password = new string('p', ParentAccounts.MaxPasswordLength + 1) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_current_session()
    {
        HttpClient client = await factory.CreateSignedInClientAsync();
        string id = (await GetCurrentSessionAsync(client)).GetProperty("id").GetString()!;

        HttpResponseMessage logout = await client.PostAsync("/api/v1/auth/logout", null);
        HttpResponseMessage after = await client.GetAsync("/api/v1/system/status");

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        Assert.False(await db.ParentSessions.AnyAsync(session => session.Id == id));
    }

    [Fact]
    public async Task Cookie_authenticated_writes_require_an_antiforgery_token()
    {
        HttpClient client = await factory.CreateSignedInClientAsync();
        HttpClient withoutToken = factory.CreateClient();
        await withoutToken.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword });
        HttpClient forgedToken = factory.CreateClient();
        await forgedToken.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword });
        forgedToken.DefaultRequestHeaders.Add(SecuritySetup.AntiforgeryHeaderName, "forged");

        HttpResponseMessage rejected = await withoutToken.PutAsJsonAsync("/api/v1/settings/csrf.test", new { value = "x" });
        HttpResponseMessage forged = await forgedToken.PutAsJsonAsync("/api/v1/settings/csrf.test", new { value = "x" });
        HttpResponseMessage logout = await withoutToken.PostAsync("/api/v1/auth/logout", null);
        HttpResponseMessage accepted = await client.PutAsJsonAsync("/api/v1/settings/csrf.test", new { value = "x" });
        HttpResponseMessage read = await withoutToken.GetAsync("/api/v1/settings");

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Api_key_writes_do_not_need_an_antiforgery_token()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/settings/key.write", new { value = "x" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account()
    {
        using AuthApiFactory fresh = new()
        {
            Settings = new Dictionary<string, string?>
            {
                ["Auth:LoginAttemptsPerMinute"] = "1000",
                ["Auth:MaxFailedLogins"] = "3",
                ["Auth:LockoutDuration"] = "00:10:00",
            },
        };
        await fresh.EnsureParentAsync();
        HttpClient client = fresh.CreateClient();

        List<HttpStatusCode> failures = [];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            HttpResponseMessage failure = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { username = ApiFactory.ParentUsername, password = "wrong password" });
            failures.Add(failure.StatusCode);
        }

        HttpResponseMessage locked = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword });

        using (IServiceScope scope = fresh.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            ParentAccount account = await db.ParentAccounts.SingleAsync();
            Assert.NotNull(account.LockoutEndsAt);
            Assert.InRange(account.LockoutEndsAt.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
            account.LockoutEndsAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        HttpResponseMessage afterLockout = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword });

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], failures);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal("application/problem+json", locked.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NoContent, afterLockout.StatusCode);
    }

    [Fact]
    public async Task Login_attempts_are_rate_limited_per_client_address()
    {
        using AuthApiFactory fresh = new()
        {
            Settings = new Dictionary<string, string?> { ["Auth:LoginAttemptsPerMinute"] = "3" },
        };
        await fresh.EnsureParentAsync();
        HttpClient client = fresh.CreateClient();

        List<HttpStatusCode> statuses = [];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = JsonContent.Create(new { username = "nobody", password = "wrong password" }),
            };
            request.Headers.Add(AuthApiFactory.RemoteIpHeader, "192.0.2.10");
            HttpResponseMessage response = await client.SendAsync(request);
            statuses.Add(response.StatusCode);
            if (attempt == 3)
            {
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            }
        }

        HttpRequestMessage otherAddress = new(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = ApiFactory.ParentUsername, password = ApiFactory.ParentPassword }),
        };
        otherAddress.Headers.Add(AuthApiFactory.RemoteIpHeader, "192.0.2.11");
        HttpResponseMessage other = await client.SendAsync(otherAddress);

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests],
            statuses);
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    }

    private static async Task<JsonElement> GetCurrentSessionAsync(HttpClient client)
    {
        List<JsonElement> sessions = await client.GetFromJsonAsync<List<JsonElement>>("/api/v1/auth/sessions") ?? [];
        return Assert.Single(sessions, session => session.GetProperty("isCurrent").GetBoolean());
    }
}
