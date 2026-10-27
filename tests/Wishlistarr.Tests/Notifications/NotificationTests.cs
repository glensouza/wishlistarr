using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Notifications;
using Wishlistarr.Api.Security;
using Wishlistarr.Core.Kids;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Notifications;

public sealed class NotificationTests
{
    [Theory]
    [InlineData("ntfy")]
    [InlineData("discord")]
    [InlineData("apprise")]
    public async Task Saved_provider_test_uses_real_http_and_keeps_secrets_encrypted(string provider)
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        server.AppriseProtocol = provider == "apprise";
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        string endpoint = new Uri(server.Address, provider == "apprise" ? "/notify" : "/topic-key").ToString();
        await SaveAsync(parent, provider, new NotificationConfiguration { Url = endpoint, Token = "fixture-secret", Targets = "json://localhost" }, enabled: false);

        HttpResponseMessage response = await parent.PostAsync($"/api/v1/notifications/{provider}/test", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        NotificationHttpRequest sent = Assert.Single(server.Requests);
        Assert.Equal(provider == "apprise" ? "/notify" : "/topic-key", sent.Path);
        if (provider == "ntfy")
        {
            Assert.Equal("Bearer fixture-secret", sent.Authorization);
            Assert.StartsWith("text/plain", sent.ContentType);
            Assert.Equal("Wishlistarr test notification.", sent.Body);
        }
        else
        {
            using JsonDocument body = JsonDocument.Parse(sent.Body);
            if (provider == "discord")
            {
                Assert.Equal("?wait=true", sent.Query);
                Assert.Equal("Wishlistarr test notification.", body.RootElement.GetProperty("content").GetString());
                Assert.Empty(body.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
                Assert.Equal(4, body.RootElement.GetProperty("flags").GetInt32());
            }
            else
            {
                Assert.Equal("Wishlistarr test notification.", body.RootElement.GetProperty("body").GetString());
                Assert.Equal("json://localhost", body.RootElement.GetProperty("urls").GetString());
                Assert.Equal("text", body.RootElement.GetProperty("format").GetString());
            }
        }

        string listed = await parent.GetStringAsync("/api/v1/notifications");
        Assert.DoesNotContain(endpoint, listed);
        Assert.DoesNotContain("fixture-secret", listed);
        Assert.DoesNotContain("json://localhost", listed);
        using IServiceScope scope = factory.Services.CreateScope();
        NotificationSubscription subscription = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().NotificationSubscriptions.SingleAsync();
        Assert.DoesNotContain(endpoint, subscription.ProtectedConfiguration);
        Assert.DoesNotContain("fixture-secret", subscription.ProtectedConfiguration);
        await SaveAsync(parent, provider, new NotificationConfiguration(), enabled: false);
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsync($"/api/v1/notifications/{provider}/test", null)).StatusCode);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Smtp_test_delivers_a_real_message_to_the_configured_recipient()
    {
        await using NotificationSmtpServer server = new();
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await SaveAsync(parent, "smtp", new NotificationConfiguration
        {
            Host = "127.0.0.1",
            Port = server.Port,
            EnableSsl = false,
            From = "sender@example.invalid",
            To = "recipient@example.invalid",
        }, enabled: false);

        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsync("/api/v1/notifications/smtp/test", null)).StatusCode);
        string mail = await server.Message.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("recipient@example.invalid", mail);
        Assert.Contains("sender@example.invalid", mail);
        Assert.Contains("Subject: Wishlistarr notification", mail);
        Assert.Contains(Convert.ToBase64String(global::System.Text.Encoding.UTF8.GetBytes("Wishlistarr test notification.")), mail);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(207)]
    [InlineData(424)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Upstream_errors_are_safe_and_redirects_are_not_followed(int status)
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        await using NotificationHttpServer other = await NotificationHttpServer.StartAsync();
        server.AppriseProtocol = true;
        server.Status = status;
        server.Redirect = new Uri(other.Address, "/private").ToString();
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await SaveAsync(parent, "apprise", new NotificationConfiguration { Url = new Uri(server.Address, "/notify/key").ToString() }, enabled: false);

        HttpResponseMessage response = await parent.PostAsync("/api/v1/notifications/apprise/test", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("upstream private error", body);
        Assert.DoesNotContain(server.Address.ToString(), body);
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task Request_events_are_durable_filtered_retried_and_not_duplicated()
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        server.Status = 503;
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await SaveAsync(parent, "ntfy", new NotificationConfiguration { Url = new Uri(server.Address, "/topic").ToString() });
        await factory.RequestAsync();

        await factory.RunAsync();
        NotificationDelivery failed = Assert.Single(await DeliveriesAsync(factory));
        Assert.Equal(1, failed.Attempts);
        Assert.Null(failed.CompletedAt);
        Assert.Contains("HTTP 503", failed.LastError);
        await factory.RunAsync();
        Assert.Single(server.Requests);
        server.Status = 200;
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await factory.RunAsync();
        NotificationDelivery success = Assert.Single(await DeliveriesAsync(factory));
        Assert.Equal(2, success.Attempts);
        Assert.NotNull(success.CompletedAt);
        Assert.Null(success.LastError);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.Contains("/notifications/confirm?token=", request.Body));
        await factory.RunAsync();
        Assert.Equal(2, server.Requests.Count);
        using IServiceScope scope = factory.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().NotificationApprovals.ToListAsync());
    }

    [Fact]
    public async Task Disabled_and_unselected_events_are_not_delivered_and_security_events_use_the_queue()
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await SaveAsync(parent, "ntfy", new NotificationConfiguration { Url = new Uri(server.Address, "/topic").ToString() }, events: ["pinFailures"]);
        await factory.RequestAsync();
        await factory.RunAsync();
        Assert.Empty(server.Requests);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IParentNotifier>().NotifyAsync(new ParentNotification(ParentNotificationKinds.PinFailures, "Repeated failed PIN attempts."));
        }

        Assert.Single(await DeliveriesAsync(factory));
        await factory.RunAsync();
        Assert.Equal("Repeated failed PIN attempts.", Assert.Single(server.Requests).Body);
        await SaveAsync(parent, "ntfy", new NotificationConfiguration(), enabled: false);
        await factory.RequestAsync(124);
        await factory.RunAsync();
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Delayed_retries_do_not_starve_ready_deliveries()
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await SaveAsync(parent, "ntfy", new NotificationConfiguration { Url = new Uri(server.Address, "/topic").ToString() }, events: ["pinFailures"]);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            int subscription = await db.NotificationSubscriptions.Select(row => row.Id).SingleAsync();
            for (int index = 0; index < 501; index++)
            {
                db.NotificationDeliveries.Add(new NotificationDelivery
                {
                    NotificationSubscriptionId = subscription,
                    Event = "pinFailures",
                    Message = "Delayed message.",
                    CreatedAt = factory.Clock.GetUtcNow(),
                    NextAttemptAt = factory.Clock.GetUtcNow() + TimeSpan.FromHours(1),
                    Attempts = 1,
                });
            }

            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IParentNotifier>().NotifyAsync(new ParentNotification(ParentNotificationKinds.PinFailures, "Ready message."));
        }

        await factory.RunAsync();
        Assert.Equal("Ready message.", Assert.Single(server.Requests).Body);
    }

    [Fact]
    public async Task Settings_and_tests_reject_anonymous_kids_and_missing_csrf()
    {
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        using HttpClient anonymous = factory.CreateClient();
        using HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync("/api/v1/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PostAsync("/api/v1/notifications/ntfy/test", null)).StatusCode);
        parent.DefaultRequestHeaders.Remove(SecuritySetup.AntiforgeryHeaderName);
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PostAsync("/api/v1/notifications/ntfy/test", null)).StatusCode);
    }

    [Theory]
    [InlineData("ftp://localhost")]
    [InlineData("https://localhost/path")]
    [InlineData("https://localhost?token=private")]
    [InlineData("https://user:pass@localhost")]
    public async Task Public_url_rejects_non_origin_values(string url)
    {
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PutAsJsonAsync("/api/v1/notifications/base-url", new { url })).StatusCode);
    }

    [Fact]
    public async Task Invalid_provider_and_smtp_credentials_without_tls_are_rejected()
    {
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        NotificationConfiguration config = new() { Host = "localhost", Port = 25, EnableSsl = false, Username = "user", Password = "fixture", From = "s@example.invalid", To = "r@example.invalid" };
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PutAsJsonAsync("/api/v1/notifications/smtp", new NotificationUpdate(false, [], config))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PutAsJsonAsync("/api/v1/notifications/unknown", new NotificationUpdate(false, [], config))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PutAsJsonAsync("/api/v1/notifications/ntfy", new { enabled = true, events = new[] { "made-up" }, configuration = new { } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.PostAsync("/api/v1/notifications/unknown/test", null)).StatusCode);
    }

    internal static async Task SaveAsync(HttpClient client, string provider, NotificationConfiguration configuration, bool enabled = true, string[]? events = null)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/v1/notifications/base-url", new { url = "https://wishlistarr.example.invalid" })).StatusCode);
        HttpResponseMessage saved = await client.PutAsJsonAsync($"/api/v1/notifications/{provider}", new NotificationUpdate(enabled, events ?? ["pending"], configuration));
        Assert.True(saved.StatusCode == HttpStatusCode.NoContent, await saved.Content.ReadAsStringAsync());
    }

    private static async Task<List<NotificationDelivery>> DeliveriesAsync(NotificationFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().NotificationDeliveries.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
    }
}
