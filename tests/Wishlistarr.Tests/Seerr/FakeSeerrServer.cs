using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wishlistarr.Core.Fulfilment;

namespace Wishlistarr.Tests.Seerr;

public sealed class FakeSeerrServer : IAsyncDisposable
{
    public const string ApiKey = "fixture-seerr-api-key";
    public const string ReportedVersion = "1.33.2";
    public const int DefaultUserId = 3;
    public const int MovieTmdbId = 603;
    public const int ShowTmdbId = 1399;

    private readonly WebApplication app;
    private readonly ConcurrentQueue<RecordedRequest> requests = new();
    private readonly List<StoredRequest> library = [];
    private int nextId = 20;

    private FakeSeerrServer(WebApplication app, Uri address)
    {
        this.app = app;
        this.Address = address;
    }

    public Uri Address { get; }

    public string? Version { get; set; } = ReportedVersion;

    public string? CommitTag { get; set; }

    public long Permissions { get; set; } = SeerrPermissions.Admin;

    public bool MissingUser { get; set; }

    public int? FailWith { get; set; }

    public string? FailureBody { get; set; }

    public Uri? RedirectTo { get; set; }

    public IReadOnlyList<RecordedRequest> Requests => [.. this.requests];

    public static async Task<FakeSeerrServer> StartAsync()
    {
        FakeSeerrServer? server = null;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.Run(context => server!.HandleAsync(context));
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeSeerrServer(app, new Uri(address.TrimEnd('/') + "/"));
        return server;
    }

    public int SeedRequest(string mediaType, int tmdbId, int status = 2)
    {
        int id = this.nextId++;
        this.library.Add(new StoredRequest(id, mediaType, tmdbId, status));
        return id;
    }

    public void SetStatus(int id, int status) => this.library.Single(item => item.Id == id).Status = status;

    public IEnumerable<RecordedRequest> Sent(string method, string path) =>
        this.requests.Where(request => request.Method == method && request.Path == path);

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
    }

    private static bool TryTrailingId(string path, string prefix, out int id)
    {
        id = 0;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string rest = path[prefix.Length..];
        return rest.Length > 0
            && !rest.Contains('/')
            && int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && id > 0;
    }

    private static int ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int number) ? number : 0;

    private static Task WriteAsync(HttpContext context, JsonNode? body, int status = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(body?.ToJsonString() ?? string.Empty);
    }

    private static async Task<JsonNode?> ReadBodyAsync(HttpContext context)
    {
        using StreamReader reader = new(context.Request.Body);
        string text = await reader.ReadToEndAsync();
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private JsonObject Title(string mediaType, int tmdbId)
    {
        JsonObject title = new()
        {
            ["id"] = tmdbId,
            ["title"] = mediaType == "movie" ? "Fixture Movie" : "Fixture Show",
        };
        StoredRequest[] matches = [.. this.library.Where(item => item.MediaType == mediaType && item.TmdbId == tmdbId)];
        if (matches.Length == 0)
        {
            return title;
        }

        JsonArray requests = [];
        foreach (StoredRequest match in matches)
        {
            requests.Add(new JsonObject { ["id"] = match.Id, ["status"] = match.Status });
        }

        title["mediaInfo"] = new JsonObject
        {
            ["tmdbId"] = tmdbId,
            ["requests"] = requests,
        };
        return title;
    }

    private async Task HandleAsync(HttpContext context)
    {
        JsonNode? body = await ReadBodyAsync(context);
        RecordedRequest request = new(
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty,
            context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()),
            context.Request.Headers.TryGetValue("X-Api-Key", out Microsoft.Extensions.Primitives.StringValues key) ? key.ToString() : null,
            body);
        this.requests.Enqueue(request);

        if (this.RedirectTo is { } redirect)
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = redirect.AbsoluteUri;
            return;
        }

        if (this.FailWith is { } failure)
        {
            context.Response.StatusCode = failure;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(this.FailureBody ?? string.Empty);
            return;
        }

        if (request.ApiKey != ApiKey)
        {
            await WriteAsync(context, new JsonObject { ["message"] = "Unauthorized" }, StatusCodes.Status401Unauthorized);
            return;
        }

        string path = request.Path;
        string method = request.Method;
        if (path == "/api/v1/status" && method == "GET")
        {
            JsonObject status = [];
            if (this.Version is not null)
            {
                status["version"] = this.Version;
            }

            if (this.CommitTag is not null)
            {
                status["commitTag"] = this.CommitTag;
            }

            await WriteAsync(context, status);
        }
        else if (TryTrailingId(path, "/api/v1/user/", out int userId) && method == "GET")
        {
            if (this.MissingUser)
            {
                await WriteAsync(context, new JsonObject { ["message"] = "Not Found" }, StatusCodes.Status404NotFound);
                return;
            }

            await WriteAsync(context, new JsonObject
            {
                ["id"] = userId,
                ["permissions"] = this.Permissions,
                ["displayName"] = "Parent",
            });
        }
        else if (TryTrailingId(path, "/api/v1/movie/", out int movieId) && method == "GET")
        {
            await WriteAsync(context, this.Title("movie", movieId));
        }
        else if (TryTrailingId(path, "/api/v1/tv/", out int showId) && method == "GET")
        {
            await WriteAsync(context, this.Title("tv", showId));
        }
        else if (path == "/api/v1/request" && method == "POST")
        {
            JsonObject payload = body as JsonObject ?? [];
            string mediaType = payload["mediaType"]?.GetValue<string>() ?? "movie";
            int tmdbId = ReadInt(payload["mediaId"]);
            int id = this.nextId++;
            this.library.Add(new StoredRequest(id, mediaType, tmdbId, 1));
            await WriteAsync(
                context,
                new JsonObject
                {
                    ["id"] = id,
                    ["status"] = 1,
                    ["media"] = new JsonObject { ["tmdbId"] = tmdbId, ["mediaType"] = mediaType },
                },
                StatusCodes.Status201Created);
        }
        else if (TryTrailingId(path, "/api/v1/request/", out int requestId) && method == "GET")
        {
            StoredRequest? found = this.library.SingleOrDefault(item => item.Id == requestId);
            if (found is null)
            {
                await WriteAsync(context, new JsonObject { ["message"] = "Not Found" }, StatusCodes.Status404NotFound);
                return;
            }

            await WriteAsync(context, new JsonObject { ["id"] = found.Id, ["status"] = found.Status });
        }
        else
        {
            await WriteAsync(context, null, StatusCodes.Status404NotFound);
        }
    }

    public sealed record RecordedRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Query,
        string? ApiKey,
        JsonNode? Body);

    private sealed class StoredRequest(int id, string mediaType, int tmdbId, int status)
    {
        public int Id { get; } = id;

        public string MediaType { get; } = mediaType;

        public int TmdbId { get; } = tmdbId;

        public int Status { get; set; } = status;
    }
}
