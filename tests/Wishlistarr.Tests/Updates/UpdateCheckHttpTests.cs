using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Wishlistarr.Api.System;
using Wishlistarr.Api.Updates;

namespace Wishlistarr.Tests.Updates;

public sealed class UpdateCheckHttpTests(LiveHandlerFixture fixture) : IClassFixture<LiveHandlerFixture>
{
    [Fact]
    public async Task A_redirect_is_not_followed_and_is_logged_by_status_only()
    {
        await fixture.ResetAsync();
        fixture.Server.Handle = context =>
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = "/follow";
            return context.Response.WriteAsync("redirect-body-secret");
        };

        await fixture.Factory.RunAsync();

        LocalRequest request = Assert.Single(fixture.Server.Requests);
        Assert.Equal("/repos/glensouza/wishlistarr/releases", request.Path);
        Assert.Contains("Update check failed with HTTP status 302.", fixture.Factory.Logs.Text(), StringComparison.Ordinal);
        Assert.DoesNotContain("redirect-body-secret", fixture.Factory.Logs.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_request_leaves_without_trace_cookie_or_account_headers()
    {
        await fixture.ResetAsync();
        fixture.Server.Handle = context => context.Response.WriteAsync("[]");

        using ActivitySource source = new("Wishlistarr.Tests.Updates");
        using ActivityListener listener = new()
        {
            ShouldListenTo = item => item.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using Activity outer = source.StartActivity("outer")!;
        Assert.NotNull(Activity.Current);

        await fixture.Factory.RunAsync();

        LocalRequest request = Assert.Single(fixture.Server.Requests);
        Assert.Equal("Wishlistarr/" + AppVersion.Current, request.Header("User-Agent"));
        Assert.Equal(UpdateChecker.AcceptMediaType, request.Header("Accept"));
        Assert.Equal(UpdateChecker.ApiVersion, request.Header("X-GitHub-Api-Version"));
        foreach (string name in new[] { "traceparent", "tracestate", "Request-Id", "baggage", "Cookie", "Authorization" })
        {
            Assert.Null(request.Header(name));
        }

        fixture.Server.Requests.Clear();
        using HttpClient control = new(new SocketsHttpHandler());
        await control.GetAsync(new Uri(fixture.Server.Address, "/control"));
        Assert.NotNull(Assert.Single(fixture.Server.Requests).Header("traceparent"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_response_over_the_size_cap_is_ignored(bool declareLength)
    {
        await fixture.ResetAsync();
        int size = UpdateChecker.MaxResponseBytes + 1;
        fixture.Server.Handle = async context =>
        {
            context.Response.ContentType = "application/json";
            if (declareLength)
            {
                context.Response.ContentLength = size;
            }

            byte[] chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)' ');
            chunk[0] = (byte)'[';
            for (int written = 0; written < size; written += chunk.Length)
            {
                await context.Response.Body.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, size - written)));
            }
        };

        await fixture.Factory.RunAsync();

        Assert.Contains("Update check failed.", fixture.Factory.Logs.Text(), StringComparison.Ordinal);
        Assert.Null(await fixture.Factory.SettingAsync(UpdateChecker.TagKey));
        Assert.NotNull(await fixture.Factory.SettingAsync(UpdateChecker.CheckedAtKey));
    }

    [Fact]
    public async Task A_response_just_under_the_size_cap_is_read()
    {
        await fixture.ResetAsync();
        SemanticVersion current = Version();
        string tag = $"v{current.Major}.{current.Minor}.{current.Patch + 1}";
        byte[] padding = new byte[UpdateChecker.MaxResponseBytes - 1024];
        Array.Fill(padding, (byte)' ');
        string json = "[{\"tag_name\":\"" + tag + "\",\"draft\":false,\"prerelease\":false}]";
        fixture.Server.Handle = async context =>
        {
            await context.Response.WriteAsync(json);
            await context.Response.Body.WriteAsync(padding);
        };

        await fixture.Factory.RunAsync();

        Assert.Equal(tag, await fixture.Factory.SettingAsync(UpdateChecker.TagKey));
    }

    [Fact]
    public async Task A_server_that_never_answers_is_abandoned_after_ten_seconds()
    {
        await fixture.ResetAsync();
        fixture.Server.Handle = context => Task.Delay(Timeout.Infinite, context.RequestAborted);

        Stopwatch watch = Stopwatch.StartNew();
        await fixture.Factory.RunAsync();
        watch.Stop();

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(30));
        Assert.Contains("Update check failed.", fixture.Factory.Logs.Text(), StringComparison.Ordinal);
        Assert.NotNull(await fixture.Factory.SettingAsync(UpdateChecker.CheckedAtKey));
        HttpResponseMessage health = await fixture.Factory.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static SemanticVersion Version()
    {
        Assert.True(SemanticVersion.TryParse(AppVersion.Current, out SemanticVersion? version));
        Assert.NotNull(version);
        return version;
    }
}

public sealed class LiveHandlerFixture : IAsyncLifetime
{
    public LocalGitHub Server { get; } = new();

    public LiveHandlerFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await this.Server.StartAsync();
        this.Factory = new LiveHandlerFactory(this.Server.Address);
    }

    public async Task ResetAsync()
    {
        await this.Factory.ResetAsync();
        this.Server.Requests.Clear();
    }

    public async Task DisposeAsync()
    {
        this.Factory?.Dispose();
        await this.Server.DisposeAsync();
    }
}

public sealed class LiveHandlerFactory(Uri server) : UpdateApiFactory
{
    protected override HttpMessageHandler CreateGitHubHandler() =>
        new RewriteToLocalHandler(server) { InnerHandler = UpdateEndpoints.CreateHandler() };
}

public sealed class RewriteToLocalHandler(Uri server) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal("api.github.com", request.RequestUri!.Host);
        request.RequestUri = new Uri(server, request.RequestUri.PathAndQuery);
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed record LocalRequest(string Path, Dictionary<string, string> Headers)
{
    public string? Header(string name) => this.Headers.TryGetValue(name, out string? value) ? value : null;
}

public sealed class LocalGitHub : IAsyncDisposable
{
    private readonly WebApplication app;

    public LocalGitHub()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        this.app = builder.Build();
        this.app.Run(async context =>
        {
            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, StringValues> header in context.Request.Headers)
            {
                headers[header.Key] = header.Value.ToString();
            }

            lock (this.Requests)
            {
                this.Requests.Add(new LocalRequest(context.Request.Path.Value ?? string.Empty, headers));
            }

            await this.Handle(context);
        });
    }

    public List<LocalRequest> Requests { get; } = [];

    public Func<HttpContext, Task> Handle { get; set; } = _ => Task.CompletedTask;

    public Uri Address => new(this.app.Urls.First());

    public Task StartAsync() => this.app.StartAsync();

    public async ValueTask DisposeAsync() => await this.app.DisposeAsync();
}
