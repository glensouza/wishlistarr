using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wishlistarr.Api.Notifications;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Security;
using Wishlistarr.Core.Kids;
using Wishlistarr.Core.Requests;
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
        Assert.Equal(1, factory.Trigger.Starts);
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
    public async Task Pending_notifications_for_requests_decided_before_delivery_are_skipped()
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await SaveAsync(parent, "ntfy", new NotificationConfiguration { Url = new Uri(server.Address, "/topic").ToString() });
        int id = await factory.RequestAsync();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaRequests>().TransitionAsync(id, RequestState.Approved, RequestActor.Parent(1, "Parent"));
        }

        await factory.RunAsync();

        Assert.Empty(server.Requests);
        NotificationDelivery skipped = Assert.Single(await DeliveriesAsync(factory));
        Assert.NotNull(skipped.CompletedAt);
        Assert.Equal(0, skipped.Attempts);
        using IServiceScope check = factory.Services.CreateScope();
        Assert.Empty(await check.ServiceProvider.GetRequiredService<WishlistarrDbContext>().NotificationApprovals.ToListAsync());
    }

    [Fact]
    public async Task Request_messages_name_the_kid_and_title_and_never_use_an_expired_title()
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await SaveAsync(parent, "ntfy", new NotificationConfiguration { Url = new Uri(server.Address, "/topic").ToString() }, events: ["pending", "denied"]);
        int id = await factory.RequestAsync();
        await factory.RunAsync();
        Assert.StartsWith("Kid 123 asked for Test @everyone title. It is waiting for your approval.\nApprove: https://wishlistarr.example.invalid/notifications/confirm?token=", Assert.Single(server.Requests).Body);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            DateTimeOffset expired = factory.Clock.GetUtcNow() - TimeSpan.FromDays(400);
            await db.MediaRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.TitleFetchedAt, expired));
            await scope.ServiceProvider.GetRequiredService<MediaRequests>().TransitionAsync(id, RequestState.Denied, RequestActor.Parent(1, "Parent"));
        }

        await factory.RunAsync();
        Assert.Equal($"Request #{id} for Kid 123 was denied.", server.Requests.Last().Body);
    }

    [Fact]
    public async Task Failed_deliveries_back_off_on_schedule_and_stop_after_eight_attempts()
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        server.Status = 500;
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await SaveAsync(parent, "ntfy", new NotificationConfiguration { Url = new Uri(server.Address, "/topic").ToString() }, events: ["pinFailures"]);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IParentNotifier>().NotifyAsync(new ParentNotification(ParentNotificationKinds.PinFailures, "Repeated failed PIN attempts."));
        }

        List<double> delays = [];
        for (int attempt = 1; attempt <= NotificationJob.MaxAttempts; attempt++)
        {
            await factory.RunAsync();
            NotificationDelivery delivery = Assert.Single(await DeliveriesAsync(factory));
            Assert.Equal(attempt, delivery.Attempts);
            TimeSpan delay = delivery.NextAttemptAt - factory.Clock.GetUtcNow();
            delays.Add(Math.Round(delay.TotalMinutes, 3));
            factory.Clock.Advance(delay);
        }

        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60], delays);
        await factory.RunAsync();
        Assert.Equal(NotificationJob.MaxAttempts, server.Requests.Count);
        NotificationDelivery exhausted = Assert.Single(await DeliveriesAsync(factory));
        Assert.Null(exhausted.CompletedAt);
        Assert.Contains("HTTP 500", exhausted.LastError);
    }

    [Fact]
    public async Task Rate_limited_deliveries_wait_for_the_retry_after_time()
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        server.Status = 429;
        server.RetryAfterSeconds = 600;
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await SaveAsync(parent, "ntfy", new NotificationConfiguration { Url = new Uri(server.Address, "/topic").ToString() }, events: ["pinFailures"]);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IParentNotifier>().NotifyAsync(new ParentNotification(ParentNotificationKinds.PinFailures, "Repeated failed PIN attempts."));
        }

        await factory.RunAsync();

        NotificationDelivery delivery = Assert.Single(await DeliveriesAsync(factory));
        Assert.Equal(10, Math.Round((delivery.NextAttemptAt - factory.Clock.GetUtcNow()).TotalMinutes, 3));
        Assert.Contains("HTTP 429", delivery.LastError);
    }

    [Fact]
    public async Task Finished_deliveries_are_removed_after_thirty_days()
    {
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await SaveAsync(parent, "ntfy", new NotificationConfiguration { Url = "https://ntfy.example.invalid/topic" }, events: ["pinFailures"]);
        DateTimeOffset now = factory.Clock.GetUtcNow();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            int subscription = await db.NotificationSubscriptions.Select(row => row.Id).SingleAsync();
            NotificationDelivery Row(string message, TimeSpan age, int attempts, bool completed) => new()
            {
                NotificationSubscriptionId = subscription,
                Event = "pinFailures",
                Message = message,
                CreatedAt = now - age,
                NextAttemptAt = now + TimeSpan.FromHours(1),
                Attempts = attempts,
                CompletedAt = completed ? now - age : null,
            };
            db.NotificationDeliveries.AddRange(
                Row("old delivered", TimeSpan.FromDays(31), 1, true),
                Row("old exhausted", TimeSpan.FromDays(31), NotificationJob.MaxAttempts, false),
                Row("old waiting", TimeSpan.FromDays(31), 1, false),
                Row("recent delivered", TimeSpan.FromDays(29), 1, true));
            await db.SaveChangesAsync();
        }

        await factory.RunAsync();

        Assert.Equal(["old waiting", "recent delivered"], (await DeliveriesAsync(factory)).Select(row => row.Message));
    }

    [Fact]
    public async Task Discord_messages_fit_the_content_limit_and_keep_both_links()
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        using NotificationFactory factory = new();
        using IServiceScope scope = factory.Services.CreateScope();
        NotificationMessage message = new(new string('T', 3000), "https://wishlistarr.example.invalid/a", "https://wishlistarr.example.invalid/d");

        await scope.ServiceProvider.GetRequiredService<NotificationSender>().SendAsync("discord",
            new NotificationConfiguration { Url = new Uri(server.Address, "/api/webhooks/1/key").ToString() }, message, CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(Assert.Single(server.Requests).Body);
        string content = body.RootElement.GetProperty("content").GetString()!;
        Assert.Equal(NotificationMessage.DiscordLimit, content.Length);
        Assert.EndsWith("…\nApprove: https://wishlistarr.example.invalid/a\nDeny: https://wishlistarr.example.invalid/d", content);
    }

    [Theory]
    [InlineData(401, "rejected the saved credentials")]
    [InlineData(404, "endpoint was not found")]
    [InlineData(302, "redirected")]
    public async Task Test_failures_explain_the_upstream_status(int status, string expected)
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        server.Status = status;
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await SaveAsync(parent, "ntfy", new NotificationConfiguration { Url = new Uri(server.Address, "/topic").ToString() }, enabled: false);

        HttpResponseMessage response = await parent.PostAsync("/api/v1/notifications/ntfy/test", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Security_notification_failures_never_break_the_kid_sign_in()
    {
        using NotificationFactory factory = new();
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        WebPushNotices webPush = scope.ServiceProvider.GetRequiredService<WebPushNotices>();
        await db.DisposeAsync();
        QueuedParentNotifier notifier = new(db, factory.Trigger, webPush, factory.Clock, NullLogger<QueuedParentNotifier>.Instance);

        await notifier.NotifyAsync(new ParentNotification(ParentNotificationKinds.ProfileLocked, "Locked."));

        Assert.Equal(0, factory.Trigger.Starts);
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
