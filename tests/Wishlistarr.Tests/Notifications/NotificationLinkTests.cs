using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Notifications;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Security;
using Wishlistarr.Api.System;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Notifications;

public sealed partial class NotificationLinkTests
{
    [Fact]
    public async Task Unfurl_gets_only_render_confirmation_and_post_records_one_parent_decision()
    {
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        using HttpClient anonymous = factory.CreateClient();
        (int id, NotificationMessage links) = await LinksAsync(factory, parent);
        string denyPath = new Uri(links.DenyUrl!).PathAndQuery;

        for (int index = 0; index < 3; index++)
        {
            HttpResponseMessage preview = await anonymous.GetAsync(denyPath);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Contains("Sign in to confirm", await preview.Content.ReadAsStringAsync());
            Assert.Equal("no-store", preview.Headers.CacheControl?.ToString());
            Assert.Equal("no-referrer", preview.Headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("form-action 'self'", preview.Headers.GetValues("Content-Security-Policy").Single());
        }

        HttpResponseMessage confirmation = await parent.GetAsync(denyPath);
        string html = await confirmation.Content.ReadAsStringAsync();
        Assert.Contains("method=\"post\"", html);
        Assert.Contains("Deny request", html);
        Assert.Contains("Test @everyone title for Kid 123", html);
        Assert.Equal(RequestState.Pending, await StateAsync(factory, id));
        Assert.Null(await UsedAtAsync(factory));

        // Submit the actual HTML form token rather than the JSON API header token.
        parent.DefaultRequestHeaders.Remove(SecuritySetup.AntiforgeryHeaderName);
        Dictionary<string, string> fields = HiddenInput().Matches(html).ToDictionary(match => WebUtility.HtmlDecode(match.Groups[1].Value), match => WebUtility.HtmlDecode(match.Groups[2].Value));
        HttpResponseMessage decision = await parent.PostAsync("/notifications/confirm", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        Assert.Equal(RequestState.Denied, await StateAsync(factory, id));
        Assert.NotNull(await UsedAtAsync(factory));
        Assert.Equal(HttpStatusCode.Gone, (await parent.PostAsync("/notifications/confirm", new FormUrlEncodedContent(fields))).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await parent.GetAsync(new Uri(links.ApproveUrl!).PathAndQuery)).StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        MediaRequestEvent item = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().MediaRequestEvents.SingleAsync(row => row.ToState == RequestState.Denied);
        Assert.Equal(RequestActorKind.Parent, item.ActorKind);
        Assert.Equal(ApiFactory.ParentUsername, item.ActorName);
        Assert.NotNull(item.ActorId);
    }

    [Fact]
    public async Task Approval_link_uses_the_existing_approval_service()
    {
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        (int id, NotificationMessage links) = await LinksAsync(factory, parent);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(parent, links.ApproveUrl!)).StatusCode);
        Assert.Equal(RequestState.Approved, await StateAsync(factory, id));
        Assert.Equal(HttpStatusCode.Gone, (await PostAsync(parent, links.DenyUrl!)).StatusCode);
    }

    [Fact]
    public async Task Expired_and_tampered_tokens_cannot_decide_or_consume_the_grant()
    {
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        (int id, NotificationMessage links) = await LinksAsync(factory, parent);
        string token = Token(links.DenyUrl!);
        string changed = token[..30] + (token[30] == 'A' ? 'B' : 'A') + token[31..];
        Assert.Equal(HttpStatusCode.Gone, (await parent.PostAsync("/notifications/confirm", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = changed }))).StatusCode);
        Assert.Null(await UsedAtAsync(factory));
        factory.Clock.Advance(NotificationLinks.Lifetime);
        Assert.Equal(HttpStatusCode.Gone, (await parent.GetAsync(new Uri(links.DenyUrl!).PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await PostAsync(parent, links.DenyUrl!)).StatusCode);
        Assert.Equal(RequestState.Pending, await StateAsync(factory, id));
        Assert.Null(await UsedAtAsync(factory));
    }

    [Fact]
    public async Task Anonymous_api_key_kid_wrong_parent_and_missing_csrf_cannot_use_a_link()
    {
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        (int id, NotificationMessage links) = await LinksAsync(factory, parent);
        using HttpClient anonymous = factory.CreateClient();
        using HttpClient apiKey = await factory.CreateAuthorizedClientAsync();
        using HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");
        using HttpClient otherParent = await SecondParentAsync(factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(anonymous, links.DenyUrl!)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(apiKey, links.DenyUrl!)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(kid, links.DenyUrl!)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await PostAsync(otherParent, links.DenyUrl!)).StatusCode);
        parent.DefaultRequestHeaders.Remove(SecuritySetup.AntiforgeryHeaderName);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(parent, links.DenyUrl!)).StatusCode);
        Assert.Equal(RequestState.Pending, await StateAsync(factory, id));
        Assert.Null(await UsedAtAsync(factory));
    }

    [Fact]
    public async Task Simultaneous_opposite_decisions_only_consume_and_apply_once()
    {
        using NotificationFactory factory = new();
        using HttpClient first = await factory.CreateSignedInClientAsync();
        using HttpClient second = await factory.CreateSignedInClientAsync();
        (int id, NotificationMessage links) = await LinksAsync(factory, first);

        HttpResponseMessage[] results = await Task.WhenAll(PostAsync(first, links.ApproveUrl!), PostAsync(second, links.DenyUrl!));

        Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Gone);
        using IServiceScope scope = factory.Services.CreateScope();
        List<MediaRequestEvent> events = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().MediaRequestEvents.Where(row => row.MediaRequestId == id && row.FromState == RequestState.Pending).ToListAsync();
        Assert.Single(events);
    }

    [Fact]
    public async Task Token_cannot_be_rebound_to_another_request_and_existing_decisions_win()
    {
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        (int id, NotificationMessage links) = await LinksAsync(factory, parent);
        int other = await factory.RequestAsync(222);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            NotificationApproval grant = await db.NotificationApprovals.SingleAsync();
            grant.MediaRequestId = other;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Gone, (await PostAsync(parent, links.DenyUrl!)).StatusCode);
        Assert.Equal(RequestState.Pending, await StateAsync(factory, id));
        Assert.Equal(RequestState.Pending, await StateAsync(factory, other));
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            NotificationApproval grant = await db.NotificationApprovals.SingleAsync();
            grant.MediaRequestId = id;
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<MediaRequests>().TransitionAsync(id, RequestState.Denied, RequestActor.Parent(1, "Parent"));
        }

        Assert.Equal(HttpStatusCode.Gone, (await PostAsync(parent, links.ApproveUrl!)).StatusCode);
        Assert.Null(await UsedAtAsync(factory));
    }

    [Fact]
    public async Task Subscriptions_are_isolated_by_parent_account_and_machine_credentials_do_not_guess_between_parents()
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        using HttpClient second = await SecondParentAsync(factory);
        await NotificationTests.SaveAsync(parent, "ntfy", new NotificationConfiguration { Url = new Uri(server.Address, "/owner").ToString() });
        await NotificationTests.SaveAsync(second, "ntfy", new NotificationConfiguration { Url = new Uri(server.Address, "/second").ToString() });
        Assert.Equal(HttpStatusCode.NoContent, (await second.PostAsync("/api/v1/notifications/ntfy/test", null)).StatusCode);
        Assert.Equal("/second", Assert.Single(server.Requests).Path);
        using HttpClient apiKey = await factory.CreateAuthorizedClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await apiKey.GetAsync("/api/v1/notifications")).StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().NotificationSubscriptions.Select(row => row.ParentAccountId).Distinct().CountAsync());
    }

    [Fact]
    public async Task Signed_links_are_redacted_and_fit_the_discord_message_limit()
    {
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        (_, NotificationMessage links) = await LinksAsync(factory, parent);
        Assert.DoesNotContain(Token(links.ApproveUrl!), LogRedactor.Redact(links.ApproveUrl));
        Assert.DoesNotContain(Token(links.DenyUrl!), LogRedactor.Redact(links.DenyUrl));
        Assert.True(links.Text.Length <= 2000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("!@#$")]
    public async Task Malformed_links_render_a_safe_unavailable_page(string token)
    {
        using NotificationFactory factory = new();
        using HttpClient anonymous = factory.CreateClient();
        HttpResponseMessage response = await anonymous.GetAsync($"/notifications/confirm?token={Uri.EscapeDataString(token)}");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Contains("Link unavailable", await response.Content.ReadAsStringAsync());
    }

    private static async Task<(int Id, NotificationMessage Links)> LinksAsync(NotificationFactory factory, HttpClient parent)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PutAsJsonAsync("/api/v1/notifications/base-url", new { url = "https://wishlistarr.example.invalid" })).StatusCode);
        int id = await factory.RequestAsync();
        using IServiceScope scope = factory.Services.CreateScope();
        int account = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().ParentAccounts.OrderBy(row => row.Id).Select(row => row.Id).FirstAsync();
        NotificationMessage links = await scope.ServiceProvider.GetRequiredService<NotificationLinks>().AddAsync(new string('T', 300), account, id, CancellationToken.None);
        return (id, links);
    }

    private static async Task<HttpClient> SecondParentAsync(NotificationFactory factory)
    {
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            ParentAccount account = new() { Username = "second-parent", PasswordHash = string.Empty, CreatedAt = factory.Clock.GetUtcNow() };
            account.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<ParentAccount>>().HashPassword(account, ApiFactory.ParentPassword);
            db.ParentAccounts.Add(account);
            await db.SaveChangesAsync();
        }

        HttpClient client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "second-parent", password = ApiFactory.ParentPassword })).StatusCode);
        await AuthApiFactory.AddAntiforgeryTokenAsync(client);
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url) =>
        client.PostAsync("/notifications/confirm", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = Token(url) }));

    private static string Token(string url) => QueryHelpers.ParseQuery(new Uri(url).Query)["token"].ToString();

    private static async Task<RequestState> StateAsync(NotificationFactory factory, int id)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().MediaRequests.Where(row => row.Id == id).Select(row => row.State).SingleAsync();
    }

    private static async Task<DateTimeOffset?> UsedAtAsync(NotificationFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().NotificationApprovals.Select(row => row.UsedAt).SingleAsync();
    }

    [GeneratedRegex("<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")]
    private static partial Regex HiddenInput();
}
