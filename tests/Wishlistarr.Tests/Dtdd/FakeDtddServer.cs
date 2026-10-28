using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wishlistarr.Tests.Dtdd;

public sealed class FakeDtddServer : IAsyncDisposable
{
    public const string ValidApiKey = "fixture-dtdd-key";

    private readonly WebApplication app;
    private readonly ConcurrentQueue<RecordedRequest> requests = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<FakeResponse>> queued = new();
    private readonly ConcurrentDictionary<string, string> lists = new();
    private readonly ConcurrentDictionary<string, string> details = new();

    private FakeDtddServer(WebApplication app, Uri address)
    {
        this.app = app;
        this.Address = address;
    }

    public Uri Address { get; }

    public Uri ApiBaseAddress => new(this.Address, "/api/v3/");

    public IReadOnlyList<RecordedRequest> Requests => [.. this.requests];

    public static async Task<FakeDtddServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        FakeDtddServer? server = null;
        app.Run(context => server!.HandleAsync(context));
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeDtddServer(app, new Uri(address));
        return server;
    }

    public void SetList(int tmdbId, string json) => this.lists[tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = json;

    public void SetDetail(int itemId, string json) => this.details[itemId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = json;

    public void Enqueue(string path, int statusCode, string? retryAfter = null, string body = "{}") =>
        this.queued.GetOrAdd(path, _ => new()).Enqueue(new FakeResponse(statusCode, body, retryAfter));

    public int Count(string path) => this.requests.Count(request => request.Path == path);

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        string? apiKey = context.Request.Headers["X-API-KEY"].ToString();
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = null;
        }

        this.requests.Enqueue(new RecordedRequest(
            path,
            context.Request.Method,
            apiKey,
            context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString())));

        if (apiKey is null)
        {
            await WriteAsync(context, StatusCodes.Status401Unauthorized, null, """{"error":"missing_api_key","message":"An API key is required."}""");
            return;
        }

        if (!string.Equals(apiKey, ValidApiKey, StringComparison.Ordinal))
        {
            await WriteAsync(context, StatusCodes.Status401Unauthorized, null, """{"error":"invalid_api_key","message":"DO-NOT-LOG-THIS"}""");
            return;
        }

        if (this.queued.TryGetValue(path, out ConcurrentQueue<FakeResponse>? queue) && queue.TryDequeue(out FakeResponse? scripted))
        {
            await WriteAsync(context, scripted.StatusCode, scripted.RetryAfter, scripted.Body);
            return;
        }

        if (path == "/api/v3/items" && context.Request.Query.TryGetValue("tmdb", out Microsoft.Extensions.Primitives.StringValues tmdb) && this.lists.TryGetValue(tmdb.ToString(), out string? list))
        {
            await WriteAsync(context, StatusCodes.Status200OK, null, list);
            return;
        }

        if (path.StartsWith("/api/v3/items/", StringComparison.Ordinal) && this.details.TryGetValue(path["/api/v3/items/".Length..], out string? detail))
        {
            await WriteAsync(context, StatusCodes.Status200OK, null, detail);
            return;
        }

        await WriteAsync(context, StatusCodes.Status404NotFound, null, """{"error":"not_found","message":"No item was found."}""");
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, string? retryAfter, string body)
    {
        context.Response.StatusCode = statusCode;
        if (retryAfter is not null)
        {
            context.Response.Headers.RetryAfter = retryAfter;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body);
    }

    public sealed record RecordedRequest(string Path, string Method, string? ApiKey, IReadOnlyDictionary<string, string> Query);

    private sealed record FakeResponse(int StatusCode, string Body, string? RetryAfter);
}
