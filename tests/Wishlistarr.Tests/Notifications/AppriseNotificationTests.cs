using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Notifications;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Notifications;

public sealed class AppriseNotificationTests
{
    [Theory]
    [InlineData("/notify", "", true, false, false)]
    [InlineData("/notify/", "json://localhost", false, true, true)]
    [InlineData("/prefix/notify", "", true, true, false)]
    [InlineData("/notify/key", "", false, true, false)]
    [InlineData("/notify/key", "json://localhost", false, true, false)]
    [InlineData("/prefix/notify/key/", "json://localhost", false, false, false)]
    [InlineData("/notify/notify", "json://localhost", false, true, false)]
    public async Task Endpoint_payload_uses_explicit_targets_defaults_or_saved_configuration(
        string path, string targets, bool defaults, bool validatesUnusedUrls, bool sendsUrls)
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        server.AppriseProtocol = true;
        server.AppriseDefaultTargets = defaults;
        server.AppriseValidatesUnusedUrls = validatesUnusedUrls;
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await NotificationTests.SaveAsync(parent, "apprise", new NotificationConfiguration { Url = new Uri(server.Address, path).ToString(), Targets = targets }, enabled: false);

        HttpResponseMessage response = await parent.PostAsync("/api/v1/notifications/apprise/test", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        NotificationHttpRequest sent = Assert.Single(server.Requests);
        Assert.Equal(path, sent.Path);
        using JsonDocument payload = JsonDocument.Parse(sent.Body);
        Assert.Equal("Wishlistarr test notification.", payload.RootElement.GetProperty("body").GetString());
        Assert.Equal(sendsUrls, payload.RootElement.TryGetProperty("urls", out JsonElement urls));
        if (sendsUrls)
        {
            Assert.Equal(targets, urls.GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_keyed_configuration_never_sends_inapplicable_targets(bool validatesUnusedUrls)
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        server.AppriseProtocol = true;
        server.AppriseValidatesUnusedUrls = validatesUnusedUrls;
        using NotificationFactory factory = new();
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NotificationSender>().SendAsync("apprise",
            new NotificationConfiguration { Url = new Uri(server.Address, "/notify/key").ToString(), Targets = "json://localhost" },
            new NotificationMessage("Notification from an existing saved configuration."), CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(server.Requests).Body);
        Assert.False(payload.RootElement.TryGetProperty("urls", out _));
    }

    [Fact]
    public async Task Switching_to_a_saved_configuration_clears_preserved_stateless_targets()
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        server.AppriseProtocol = true;
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await NotificationTests.SaveAsync(parent, "apprise", new NotificationConfiguration { Url = new Uri(server.Address, "/notify").ToString(), Targets = "json://localhost" }, enabled: false);
        await NotificationTests.SaveAsync(parent, "apprise", new NotificationConfiguration { Url = new Uri(server.Address, "/notify/key").ToString() }, enabled: false);

        using IServiceScope scope = factory.Services.CreateScope();
        NotificationSubscription subscription = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().NotificationSubscriptions.SingleAsync();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<NotificationSettings>().Read(subscription).Targets);
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PostAsync("/api/v1/notifications/apprise/test", null)).StatusCode);
        using JsonDocument payload = JsonDocument.Parse(Assert.Single(server.Requests).Body);
        Assert.False(payload.RootElement.TryGetProperty("urls", out _));
    }

    [Fact]
    public async Task Stateless_notification_without_targets_or_server_defaults_reports_failure()
    {
        await using NotificationHttpServer server = await NotificationHttpServer.StartAsync();
        server.AppriseProtocol = true;
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        await NotificationTests.SaveAsync(parent, "apprise", new NotificationConfiguration { Url = new Uri(server.Address, "/notify").ToString() }, enabled: false);

        HttpResponseMessage response = await parent.PostAsync("/api/v1/notifications/apprise/test", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("HTTP 400", await response.Content.ReadAsStringAsync());
        using JsonDocument payload = JsonDocument.Parse(Assert.Single(server.Requests).Body);
        Assert.False(payload.RootElement.TryGetProperty("urls", out _));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/notify/key/extra")]
    [InlineData("/not-notify")]
    [InlineData("/notify//")]
    public async Task Invalid_notification_paths_are_rejected_before_delivery(string path)
    {
        using NotificationFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        HttpResponseMessage response = await parent.PutAsJsonAsync("/api/v1/notifications/apprise",
            new NotificationUpdate(false, [], new NotificationConfiguration { Url = $"https://apprise.example.invalid{path}" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
