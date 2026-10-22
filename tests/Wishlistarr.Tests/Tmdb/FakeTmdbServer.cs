using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wishlistarr.Tests.Tmdb;

public sealed class FakeTmdbServer : IAsyncDisposable
{
    public const string ValidApiKey = "valid-test-key";

    public static readonly byte[] ImageBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00,
    ];

    private readonly WebApplication app;
    private readonly ConcurrentQueue<RecordedRequest> requests = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<FakeResponse>> queued = new();
    private readonly ConcurrentDictionary<string, string> bodies = new();

    private FakeTmdbServer(WebApplication app, Uri address)
    {
        this.app = app;
        this.Address = address;
        foreach ((string path, string body) in DefaultBodies())
        {
            this.bodies[path] = body;
        }
    }

    public Uri Address { get; }

    public Uri ApiBaseAddress => new(this.Address, "/3/");

    public Uri ImageBaseAddress => new(this.Address, "/t/p/");

    public IReadOnlyList<RecordedRequest> Requests => [.. this.requests];

    public static async Task<FakeTmdbServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        FakeTmdbServer? server = null;
        app.Run(context => server!.HandleAsync(context));
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeTmdbServer(app, new Uri(address));
        return server;
    }

    public void SetBody(string path, string json) => this.bodies[path] = json;

    public void Enqueue(string path, int statusCode, string? retryAfter = null, string body = "{}") =>
        this.queued.GetOrAdd(path, _ => new()).Enqueue(new FakeResponse(statusCode, body, retryAfter));

    public int Count(string path) => this.requests.Count(request => request.Path == path);

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
    }

    private static IEnumerable<KeyValuePair<string, string>> DefaultBodies()
    {
        const string Page = """
            {"page":1,"results":[{"id":603,"media_type":"movie","title":"The Matrix","poster_path":"/matrix.jpg","vote_average":8.2,"genre_ids":[28,878],"original_language":"en"}],"total_pages":1,"total_results":1}
            """;
        yield return new("/3/configuration", """{"images":{"base_url":"http://image.tmdb.org/t/p/"}}""");
        yield return new("/3/search/multi", Page);
        yield return new("/3/search/movie", Page);
        yield return new("/3/search/tv", Page);
        yield return new("/3/discover/movie", Page);
        yield return new("/3/discover/tv", Page);
        yield return new("/3/trending/movie/week", Page);
        yield return new("/3/trending/tv/day", Page);
        yield return new("/3/movie/603", """{"id":603,"title":"The Matrix","overview":"A hacker learns the truth.","runtime":136,"genres":[{"id":28,"name":"Action"}],"original_language":"en","vote_average":8.2}""");
        yield return new("/3/tv/1399", """{"id":1399,"name":"Show","number_of_seasons":8,"genres":[],"vote_average":8.4}""");
        yield return new("/3/movie/603/credits", """{"cast":[{"id":6384,"name":"Actor","character":"Neo","profile_path":"/a.jpg"}],"crew":[{"id":9340,"name":"Director","job":"Director","department":"Directing"}]}""");
        yield return new("/3/movie/603/videos", """{"results":[{"id":"1","key":"abc","name":"Trailer","site":"YouTube","type":"Trailer","official":true}]}""");
        yield return new("/3/movie/603/images", """{"posters":[{"file_path":"/p.jpg","width":500,"height":750,"iso_639_1":"en"}],"backdrops":[],"logos":[]}""");
        yield return new("/3/movie/603/watch/providers", """{"results":{"US":{"link":"https://example.test","flatrate":[{"provider_id":8,"provider_name":"Stream","logo_path":"/s.jpg"}]}}}""");
        yield return new("/3/movie/603/release_dates", """
            {"results":[
              {"iso_3166_1":"GB","release_dates":[{"certification":"15","descriptors":[],"type":3}]},
              {"iso_3166_1":"US","release_dates":[
                {"certification":"","descriptors":[],"type":1},
                {"certification":"PG-13","descriptors":["Violence"],"type":3},
                {"certification":"R","descriptors":[],"type":5}]},
              {"iso_3166_1":"FR","release_dates":[{"certification":"","descriptors":[],"type":3}]}]}
            """);
        yield return new("/3/tv/1399/content_ratings", """
            {"results":[
              {"iso_3166_1":"DE","rating":"16","descriptors":[]},
              {"iso_3166_1":"US","rating":"TV-MA","descriptors":["Language","Violence"]},
              {"iso_3166_1":"GB","rating":"","descriptors":[]}]}
            """);
    }

    private async Task HandleAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        string? bearer = context.Request.Headers.Authorization.ToString();
        this.requests.Enqueue(new RecordedRequest(
            path,
            context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()),
            string.IsNullOrEmpty(bearer) ? null : bearer));

        if (path.StartsWith("/t/p/", StringComparison.Ordinal))
        {
            await HandleImageAsync(context, path);
            return;
        }

        if (this.queued.TryGetValue(path, out ConcurrentQueue<FakeResponse>? queue) && queue.TryDequeue(out FakeResponse? scripted))
        {
            context.Response.StatusCode = scripted.StatusCode;
            if (scripted.RetryAfter is not null)
            {
                context.Response.Headers.RetryAfter = scripted.RetryAfter;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(scripted.Body);
            return;
        }

        string? key = context.Request.Query["api_key"];
        bool authorized = key == ValidApiKey || bearer == $"Bearer {ValidApiKey}.token";
        if (!authorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"status_code":7,"status_message":"Invalid API key."}""");
            return;
        }

        if (!this.bodies.TryGetValue(path, out string? body))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body);
    }

    private static async Task HandleImageAsync(HttpContext context, string path)
    {
        if (path.EndsWith("/redirect.jpg", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = "/t/p/w500/after-redirect.jpg";
            return;
        }

        if (path.EndsWith("/missing.jpg", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (path.EndsWith("/page.jpg", StringComparison.Ordinal))
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html></html>");
            return;
        }

        if (path.EndsWith("/huge.jpg", StringComparison.Ordinal))
        {
            context.Response.ContentType = "image/jpeg";
            byte[] chunk = new byte[1024 * 1024];
            for (int index = 0; index < 11; index++)
            {
                await context.Response.Body.WriteAsync(chunk);
            }

            return;
        }

        context.Response.ContentType = "image/png";
        await context.Response.Body.WriteAsync(ImageBytes);
    }

    public sealed record RecordedRequest(string Path, IReadOnlyDictionary<string, string> Query, string? Authorization);

    private sealed record FakeResponse(int StatusCode, string Body, string? RetryAfter);
}
