using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wishlistarr.Core.Fulfilment;

namespace Wishlistarr.Tests.Arr;

public sealed class FakeArrServer : IAsyncDisposable
{
    public const string ApiKey = "fixture-arr-api-key";
    public const int ShowTmdbId = 1000;
    public const int ShowTvdbId = 2000;
    public const int MovieTmdbId = 603;

    private readonly WebApplication app;
    private readonly ConcurrentQueue<RecordedRequest> requests = new();
    private readonly List<JsonObject> library = [];
    private readonly List<JsonObject> tags = [];
    private int nextId = 100;

    private FakeArrServer(ArrService service, WebApplication app, Uri address)
    {
        this.Service = service;
        this.app = app;
        this.Address = address;
        this.tags.Add(new JsonObject { ["id"] = 1, ["label"] = "kids" });
    }

    public ArrService Service { get; }

    public Uri Address { get; }

    public string AppName { get; set; } = string.Empty;

    public int? FailWith { get; set; }

    public bool DropNextAddResponse { get; set; }

    public bool HideNextLookup { get; set; }

    public IReadOnlyList<int>? LookupSeasons { get; set; }

    public IReadOnlyList<RecordedRequest> Requests => [.. this.requests];

    public IReadOnlyList<JsonObject> Library => [.. this.library];

    public IReadOnlyList<JsonObject> Tags => [.. this.tags];

    public static async Task<FakeArrServer> StartAsync(ArrService service)
    {
        FakeArrServer? server = null;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.Run(context => server!.HandleAsync(context));
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeArrServer(service, app, new Uri(address.TrimEnd('/') + "/")) { AppName = service.ToString() };
        return server;
    }

    public JsonObject SeedSeries(bool monitored, params int[] monitoredSeasons)
    {
        JsonObject series = ShowTemplate();
        series["id"] = this.nextId++;
        series["monitored"] = monitored;
        series["tags"] = new JsonArray();
        foreach (JsonNode? season in series["seasons"]!.AsArray())
        {
            season!["monitored"] = monitoredSeasons.Contains(season["seasonNumber"]!.GetValue<int>());
        }

        this.library.Add(series);
        return series;
    }

    public JsonObject SeedMovie(bool monitored, bool hasFile)
    {
        JsonObject movie = MovieTemplate();
        movie["id"] = this.nextId++;
        movie["monitored"] = monitored;
        movie["hasFile"] = hasFile;
        movie["tags"] = new JsonArray();
        this.library.Add(movie);
        return movie;
    }

    public void SeedTag(int id, string label) => this.tags.Add(new JsonObject { ["id"] = id, ["label"] = label });

    public IEnumerable<RecordedRequest> Sent(string method, string path) =>
        this.requests.Where(request => request.Method == method && request.Path == path);

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
    }

    private static JsonObject ShowTemplate() => new()
    {
        ["title"] = "Fixture Show",
        ["tmdbId"] = ShowTmdbId,
        ["tvdbId"] = ShowTvdbId,
        ["year"] = 2020,
        ["titleSlug"] = "fixture-show",
        ["seasons"] = new JsonArray(
            Season(0),
            Season(1),
            Season(2),
            Season(3)),
    };

    private static JsonObject MovieTemplate() => new()
    {
        ["title"] = "Fixture Movie",
        ["tmdbId"] = MovieTmdbId,
        ["year"] = 1999,
        ["titleSlug"] = "603",
    };

    private static JsonObject Season(int number) => new() { ["seasonNumber"] = number, ["monitored"] = false };

    private JsonArray ExistsFailure(string field) => new(new JsonObject
    {
        ["propertyName"] = field == "tvdbId" ? "TvdbId" : "TmdbId",
        ["errorMessage"] = this.Service == ArrService.Sonarr ? "This series has already been added" : "This movie has already been added",
        ["severity"] = "error",
        ["errorCode"] = this.Service == ArrService.Sonarr ? "SeriesExistsValidator" : "MovieExistsValidator",
    });

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

        if (this.FailWith is { } failure)
        {
            await WriteAsync(context, null, failure);
            return;
        }

        if (request.ApiKey != ApiKey)
        {
            await WriteAsync(context, new JsonObject { ["error"] = "Unauthorized" }, StatusCodes.Status401Unauthorized);
            return;
        }

        string path = request.Path;
        string method = request.Method;
        string resource = this.Service == ArrService.Sonarr ? "series" : "movie";

        if (path == "/api/v3/system/status")
        {
            await WriteAsync(context, new JsonObject { ["appName"] = this.AppName, ["version"] = "4.0.9.2244" });
        }
        else if (path == "/api/v3/qualityprofile")
        {
            await WriteAsync(context, new JsonArray(
                new JsonObject { ["id"] = 4, ["name"] = "HD-1080p" },
                new JsonObject { ["id"] = 6, ["name"] = "Any" }));
        }
        else if (path == "/api/v3/rootfolder")
        {
            await WriteAsync(context, new JsonArray(
                new JsonObject { ["id"] = 1, ["path"] = "/media/kids", ["freeSpace"] = 123456789012L }));
        }
        else if (path == "/api/v3/tag" && method == "GET")
        {
            await WriteAsync(context, new JsonArray([.. this.tags.Select(tag => (JsonNode)tag.DeepClone())]));
        }
        else if (path == "/api/v3/tag" && method == "POST")
        {
            JsonObject created = new() { ["id"] = this.tags.Count + 1, ["label"] = body!["label"]!.GetValue<string>() };
            this.tags.Add(created);
            await WriteAsync(context, created, StatusCodes.Status201Created);
        }
        else if (path == "/api/v3/command" && method == "POST")
        {
            await WriteAsync(context, new JsonObject { ["id"] = 1 }, StatusCodes.Status201Created);
        }
        else if (path == "/api/v3/series/lookup" && this.Service == ArrService.Sonarr)
        {
            string term = request.Query.GetValueOrDefault("term", string.Empty);
            JsonObject show = ShowTemplate();
            if (this.LookupSeasons is { } seasons)
            {
                show["seasons"] = new JsonArray([.. seasons.Select(number => (JsonNode)Season(number))]);
            }

            JsonArray results = term == $"tmdb:{ShowTmdbId}" ? [show] : [];
            await WriteAsync(context, results);
        }
        else if (path == "/api/v3/movie/lookup/tmdb" && this.Service == ArrService.Radarr)
        {
            bool known = request.Query.GetValueOrDefault("tmdbId") == MovieTmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await WriteAsync(context, known ? MovieTemplate() : null, known ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
        }
        else if (path == $"/api/v3/{resource}" && method == "GET")
        {
            string field = this.Service == ArrService.Sonarr ? "tvdbId" : "tmdbId";
            string wanted = request.Query.GetValueOrDefault(field, string.Empty);
            if (this.HideNextLookup)
            {
                this.HideNextLookup = false;
                await WriteAsync(context, new JsonArray());
                return;
            }

            await WriteAsync(context, new JsonArray([.. this.library
                .Where(item => item[field]!.ToString() == wanted)
                .Select(item => (JsonNode)item.DeepClone())]));
        }
        else if (path == $"/api/v3/{resource}" && method == "POST")
        {
            string field = this.Service == ArrService.Sonarr ? "tvdbId" : "tmdbId";
            if (this.library.Any(item => item[field]!.ToString() == body![field]!.ToString()))
            {
                await WriteAsync(context, this.ExistsFailure(field), StatusCodes.Status400BadRequest);
                return;
            }

            JsonObject added = (JsonObject)body!.DeepClone().AsObject();
            added["id"] = this.nextId++;
            this.library.Add(added);
            if (this.DropNextAddResponse)
            {
                this.DropNextAddResponse = false;
                context.Abort();
                return;
            }

            await WriteAsync(context, added, StatusCodes.Status201Created);
        }
        else if (path.StartsWith($"/api/v3/{resource}/", StringComparison.Ordinal) && method == "PUT")
        {
            int id = int.Parse(path[(path.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            int index = this.library.FindIndex(item => item["id"]!.GetValue<int>() == id);
            this.library[index] = (JsonObject)body!.DeepClone().AsObject();
            await WriteAsync(context, body, StatusCodes.Status202Accepted);
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
}
