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

namespace Wishlistarr.Tests.Seerr;

public sealed class FakeSeerrServer : IAsyncDisposable
{
    public const string ApiKey = "fixture-seerr-api-key";
    public const string ReportedVersion = "1.33.2";
    public const int AdminUserId = 1;
    public const int DefaultUserId = 3;
    public const int MovieTmdbId = 603;
    public const int ShowTmdbId = 1399;

    public const long Admin = 2;
    public const long ManageUsers = 8;
    public const long ManageRequests = 16;
    public const long Request = 32;
    public const long AutoApprove = 128;
    public const long AutoApproveMovie = 256;
    public const long AutoApproveTv = 512;
    public const long RequestView = 16384;
    public const long RequestMovie = 262144;
    public const long RequestTv = 524288;

    public const int RequestPending = 1;
    public const int RequestApproved = 2;
    public const int RequestDeclined = 3;
    public const int RequestFailed = 4;
    public const int RequestCompleted = 5;

    public const int MediaUnknown = 1;
    public const int MediaPending = 2;
    public const int MediaProcessing = 3;
    public const int MediaPartiallyAvailable = 4;
    public const int MediaAvailable = 5;
    public const int MediaBlocklisted = 6;
    public const int MediaDeleted = 7;
    public const int OverseerrMediaDeleted = 6;

    private static readonly int[] ShowSeasons = [1, 2, 3];

    private readonly WebApplication app;
    private readonly ConcurrentQueue<RecordedRequest> requests = new();
    private readonly Lock gate = new();
    private readonly Dictionary<int, long> users = new() { [AdminUserId] = Admin, [DefaultUserId] = Admin };
    private readonly List<StoredRequest> library = [];
    private readonly Dictionary<(string MediaType, int TmdbId), StoredMedia> media = [];
    private int nextId = 20;

    private FakeSeerrServer(WebApplication app, Uri address)
    {
        this.app = app;
        this.Address = address;
    }

    public Uri Address { get; }

    public string? Version { get; set; } = ReportedVersion;

    public string? CommitTag { get; set; }

    public long Permissions
    {
        get => this.users[DefaultUserId];
        set => this.users[DefaultUserId] = value;
    }

    public bool MissingUser { get; set; }

    public int? FailWith { get; set; }

    public string? FailureBody { get; set; }

    public Uri? RedirectTo { get; set; }

    public string? PathBase { get; set; }

    public bool Overseerr { get; set; }

    public Action? BeforeNextCreate { get; set; }

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

    public int SeedRequest(
        string mediaType,
        int tmdbId,
        int status = RequestApproved,
        bool is4k = false,
        int[]? seasons = null,
        int requestedBy = AdminUserId)
    {
        lock (this.gate)
        {
            int id = this.nextId++;
            this.library.Add(new StoredRequest(id, mediaType, tmdbId, is4k, seasons ?? [], requestedBy) { Status = status });
            this.MediaFor(mediaType, tmdbId);
            return id;
        }
    }

    public void SetStatus(int id, int status)
    {
        lock (this.gate)
        {
            this.library.Single(item => item.Id == id).Status = status;
        }
    }

    public void SetMediaStatus(string mediaType, int tmdbId, int status)
    {
        lock (this.gate)
        {
            this.MediaFor(mediaType, tmdbId).Status = status;
        }
    }

    public void SetSeasonStatus(int tmdbId, int season, int status)
    {
        lock (this.gate)
        {
            this.MediaFor("tv", tmdbId).Seasons[season] = status;
        }
    }

    public IEnumerable<RecordedRequest> Sent(string method, string path) =>
        this.requests.Where(request => request.Method == method && request.Path == (this.PathBase ?? string.Empty) + path);

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

    private static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int number) ? number : null;

    private static async Task<JsonNode?> ReadBodyAsync(HttpContext context)
    {
        using StreamReader reader = new(context.Request.Body);
        string text = await reader.ReadToEndAsync();
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private static Reply Message(int status, string message) => new(status, new JsonObject { ["message"] = message });

    private static Reply Forbidden() => new(
        StatusCodes.Status403Forbidden,
        new JsonObject { ["status"] = 403, ["error"] = "You do not have permission to access this endpoint" });

    private static bool Has(long permissions, long wanted) => (permissions & (Admin | wanted)) != 0;

    private StoredMedia MediaFor(string mediaType, int tmdbId)
    {
        if (!this.media.TryGetValue((mediaType, tmdbId), out StoredMedia? found))
        {
            found = new StoredMedia { Status = MediaPending };
            this.media[(mediaType, tmdbId)] = found;
        }

        return found;
    }

    private JsonObject RequestJson(StoredRequest request, bool withMedia)
    {
        JsonArray seasons = [];
        foreach (int season in request.Seasons)
        {
            seasons.Add(new JsonObject { ["seasonNumber"] = season, ["status"] = request.Status });
        }

        JsonObject json = new()
        {
            ["id"] = request.Id,
            ["status"] = request.Status,
            ["type"] = request.MediaType,
            ["is4k"] = request.Is4k,
            ["seasons"] = seasons,
            ["requestedBy"] = new JsonObject { ["id"] = request.RequestedBy, ["displayName"] = "User " + request.RequestedBy },
        };
        if (withMedia)
        {
            StoredMedia media = this.MediaFor(request.MediaType, request.TmdbId);
            json["media"] = new JsonObject
            {
                ["tmdbId"] = request.TmdbId,
                ["mediaType"] = request.MediaType,
                ["status"] = media.Status,
                ["status4k"] = MediaUnknown,
            };
        }

        return json;
    }

    private JsonObject Title(string mediaType, int tmdbId)
    {
        JsonObject title = new()
        {
            ["id"] = tmdbId,
            [mediaType == "movie" ? "title" : "name"] = mediaType == "movie" ? "Fixture Movie" : "Fixture Show",
        };
        if (mediaType == "tv")
        {
            JsonArray tmdbSeasons = [new JsonObject { ["seasonNumber"] = 0, ["episodeCount"] = 2 }];
            foreach (int season in ShowSeasons)
            {
                tmdbSeasons.Add(new JsonObject { ["seasonNumber"] = season, ["episodeCount"] = 8 });
            }

            title["seasons"] = tmdbSeasons;
        }

        if (!this.media.TryGetValue((mediaType, tmdbId), out StoredMedia? stored))
        {
            return title;
        }

        JsonArray requests = [];
        foreach (StoredRequest match in this.library.Where(item => item.MediaType == mediaType && item.TmdbId == tmdbId))
        {
            requests.Add(this.RequestJson(match, false));
        }

        JsonArray seasons = [];
        foreach ((int season, int status) in stored.Seasons.OrderBy(pair => pair.Key))
        {
            seasons.Add(new JsonObject { ["seasonNumber"] = season, ["status"] = status, ["status4k"] = MediaUnknown });
        }

        title["mediaInfo"] = new JsonObject
        {
            ["tmdbId"] = tmdbId,
            ["mediaType"] = mediaType,
            ["status"] = stored.Status,
            ["status4k"] = MediaUnknown,
            ["requests"] = requests,
            ["seasons"] = seasons,
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
            context.Request.Headers.TryGetValue("X-Api-User", out Microsoft.Extensions.Primitives.StringValues user) ? user.ToString() : null,
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

        Reply reply;
        lock (this.gate)
        {
            reply = this.Route(request);
        }

        context.Response.StatusCode = reply.Status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(reply.Body?.ToJsonString() ?? string.Empty);
    }

    private Reply Route(RecordedRequest request)
    {
        string path = request.Path;
        if (this.PathBase is { } pathBase)
        {
            if (!path.StartsWith(pathBase + "/", StringComparison.Ordinal))
            {
                return new Reply(StatusCodes.Status404NotFound, null);
            }

            path = path[pathBase.Length..];
        }

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

            return new Reply(StatusCodes.Status200OK, status);
        }

        if (path == "/api/v1/settings/public" && method == "GET")
        {
            JsonObject settings = new() { ["applicationTitle"] = "Fixture", ["initialized"] = true };
            if (!this.Overseerr)
            {
                settings["mediaServerType"] = 1;
            }

            return new Reply(StatusCodes.Status200OK, settings);
        }

        int actor = AdminUserId;
        if (request.ApiUser is not null && !int.TryParse(request.ApiUser, NumberStyles.None, CultureInfo.InvariantCulture, out actor))
        {
            return Forbidden();
        }

        if (request.ApiKey != ApiKey || !this.UserExists(actor))
        {
            return Forbidden();
        }

        if (TryTrailingId(path, "/api/v1/user/", out int userId) && method == "GET")
        {
            return this.UserExists(userId)
                ? new Reply(StatusCodes.Status200OK, new JsonObject
                {
                    ["id"] = userId,
                    ["permissions"] = this.users[userId],
                    ["displayName"] = "User " + userId,
                })
                : Message(StatusCodes.Status404NotFound, "User not found.");
        }

        if (TryTrailingId(path, "/api/v1/movie/", out int movieId) && method == "GET")
        {
            return new Reply(StatusCodes.Status200OK, this.Title("movie", movieId));
        }

        if (TryTrailingId(path, "/api/v1/tv/", out int showId) && method == "GET")
        {
            return new Reply(StatusCodes.Status200OK, this.Title("tv", showId));
        }

        if (path == "/api/v1/request" && method == "POST")
        {
            Action? before = this.BeforeNextCreate;
            this.BeforeNextCreate = null;
            before?.Invoke();
            return this.Create(request.Body as JsonObject ?? [], actor);
        }

        if (TryTrailingId(path, "/api/v1/request/", out int requestId) && method == "GET")
        {
            StoredRequest? found = this.library.SingleOrDefault(item => item.Id == requestId);
            if (found is null)
            {
                return Message(StatusCodes.Status404NotFound, "Request not found.");
            }

            if (found.RequestedBy != actor && !Has(this.users[actor], ManageRequests | RequestView))
            {
                return Message(StatusCodes.Status403Forbidden, "You do not have permission to view this request.");
            }

            return new Reply(StatusCodes.Status200OK, this.RequestJson(found, true));
        }

        return new Reply(StatusCodes.Status404NotFound, null);
    }

    private bool UserExists(int id) => this.users.ContainsKey(id) && !(this.MissingUser && id == DefaultUserId);

    private Reply Create(JsonObject payload, int actor)
    {
        string? mediaType = payload["mediaType"] is JsonValue type && type.TryGetValue(out string? text) ? text : null;
        if (mediaType is not ("movie" or "tv") || ReadInt(payload["mediaId"]) is not int tmdbId)
        {
            return Message(StatusCodes.Status400BadRequest, "request.body should have required property 'mediaType'");
        }

        bool is4k = payload["is4k"] is JsonValue flag && flag.TryGetValue(out bool value) && value;
        int requestUser = actor;
        if (ReadInt(payload["userId"]) is int bodyUser)
        {
            if (!Has(this.users[actor], ManageUsers | ManageRequests))
            {
                return Message(StatusCodes.Status403Forbidden, "You do not have permission to modify the request user.");
            }

            requestUser = bodyUser;
        }

        bool movie = mediaType == "movie";
        if (!Has(this.users[requestUser], Request | (movie ? RequestMovie : RequestTv)))
        {
            return Message(StatusCodes.Status403Forbidden, movie
                ? "You do not have permission to make movie requests."
                : "You do not have permission to make series requests.");
        }

        bool approved = Has(this.users[actor], AutoApprove | ManageRequests | (movie ? AutoApproveMovie : AutoApproveTv));
        StoredMedia media = this.MediaFor(mediaType, tmdbId);
        int deleted = this.Overseerr ? OverseerrMediaDeleted : MediaDeleted;
        if (media.Status == MediaUnknown || media.Status == deleted)
        {
            media.Status = MediaPending;
        }

        StoredRequest[] existing = [.. this.library.Where(item => item.MediaType == mediaType && item.TmdbId == tmdbId && item.Is4k == is4k)];
        int[] seasons = [];
        if (movie)
        {
            if (existing.Length > 0 && existing[0].Status is not (RequestDeclined or RequestCompleted))
            {
                return Message(StatusCodes.Status409Conflict, "Request for this media already exists.");
            }
        }
        else
        {
            int[] requested;
            if (payload["seasons"] is JsonValue all && all.TryGetValue(out string? word) && word == "all")
            {
                requested = ShowSeasons;
            }
            else if (payload["seasons"] is JsonArray list)
            {
                requested = [.. list.Select(item => ReadInt(item) ?? -1)];
            }
            else
            {
                return Message(StatusCodes.Status500InternalServerError, "Cannot read properties of undefined (reading 'filter')");
            }

            HashSet<int> covered = [.. existing
                .Where(item => item.Status is not (RequestDeclined or RequestCompleted))
                .SelectMany(item => item.Seasons)];
            covered.UnionWith(media.Seasons.Where(pair => pair.Value != MediaUnknown && pair.Value != deleted).Select(pair => pair.Key));
            seasons = [.. requested.Where(season => !covered.Contains(season))];
            if (seasons.Length == 0)
            {
                return Message(StatusCodes.Status202Accepted, "No seasons available to request");
            }
        }

        StoredRequest created = new(this.nextId++, mediaType, tmdbId, is4k, seasons, requestUser)
        {
            Status = approved ? RequestApproved : RequestPending,
        };
        this.library.Add(created);
        return new Reply(StatusCodes.Status201Created, this.RequestJson(created, true));
    }

    public sealed record RecordedRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Query,
        string? ApiKey,
        string? ApiUser,
        JsonNode? Body);

    private sealed record Reply(int Status, JsonNode? Body);

    private sealed class StoredRequest(int id, string mediaType, int tmdbId, bool is4k, int[] seasons, int requestedBy)
    {
        public int Id { get; } = id;

        public string MediaType { get; } = mediaType;

        public int TmdbId { get; } = tmdbId;

        public bool Is4k { get; } = is4k;

        public int[] Seasons { get; } = seasons;

        public int RequestedBy { get; } = requestedBy;

        public int Status { get; set; }
    }

    private sealed class StoredMedia
    {
        public int Status { get; set; }

        public Dictionary<int, int> Seasons { get; } = [];
    }
}
