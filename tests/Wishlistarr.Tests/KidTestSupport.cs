using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wishlistarr.Core.Kids;

namespace Wishlistarr.Tests;

public sealed class TestClock : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (this.gate)
        {
            return this.now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (this.gate)
        {
            this.now += by;
        }
    }
}

public sealed class RecordingNotifier : IParentNotifier
{
    private readonly List<ParentNotification> sent = [];

    public IReadOnlyList<ParentNotification> Sent
    {
        get
        {
            lock (this.sent)
            {
                return [.. this.sent];
            }
        }
    }

    public Task NotifyAsync(ParentNotification notification, CancellationToken ct = default)
    {
        lock (this.sent)
        {
            this.sent.Add(notification);
        }

        return Task.CompletedTask;
    }
}

public sealed class KidApiFactory : AuthApiFactory
{
    public TestClock Clock { get; } = new();

    public RecordingNotifier Notifier { get; } = new();

    public static Task<HttpResponseMessage> SignInAsync(HttpClient device, int profileId, string? pin) =>
        device.PostAsJsonAsync("/api/v1/kid/signin", new { profileId, pin });

    public async Task<JsonElement> CreateProfileAsync(
        HttpClient parent,
        string name,
        string? pin = null,
        int? movieAgeLimit = null,
        int? tvAgeLimit = null,
        string? plexLabel = null)
    {
        HttpResponseMessage response = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name, avatar = "fox", pin, movieAgeLimit, tvAgeLimit, plexLabel });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public HttpClient CreateDevice() => this.CreateClient();

    public async Task<HttpResponseMessage> FailAndWaitAsync(HttpClient device, int profileId, string wrongPin)
    {
        HttpResponseMessage response = await SignInAsync(device, profileId, wrongPin);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        HttpResponseMessage probe = await SignInAsync(device, profileId, "0000");
        if (probe.Headers.RetryAfter?.Delta is TimeSpan wait)
        {
            this.Clock.Advance(wait);
        }

        return response;
    }

    public static async Task<int> GetBackoffSecondsAsync(HttpClient device, int profileId)
    {
        HttpResponseMessage probe = await SignInAsync(device, profileId, "0000");
        Assert.Equal(HttpStatusCode.TooManyRequests, probe.StatusCode);
        return (int)probe.Headers.RetryAfter!.Delta!.Value.TotalSeconds;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(this.Clock);
            services.RemoveAll<IParentNotifier>();
            services.AddSingleton<IParentNotifier>(this.Notifier);
        });
    }
}
