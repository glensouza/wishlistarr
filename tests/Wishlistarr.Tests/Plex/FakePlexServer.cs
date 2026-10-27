using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wishlistarr.Tests.Plex;

public sealed partial class FakePlexServer : IAsyncDisposable
{
    public const string AccountToken = "fixture-account-token";
    public const string ServerToken = "fixture-server-token";
    public const string MachineIdentifier = "4b1e0c9d8a7f6e5d4c3b2a1908f7e6d5c4b3a291";
    public const string ImpostorMachineIdentifier = "ffffffffffffffffffffffffffffffffffffffff";
    public const string SharedMachineIdentifier = "shared-machine-fixture";
    public const long PinId = 81726354;
    public const string PinCode = "x9q2kz7m4w8rv3ntb6hd5yc1p";
    public const string AccountName = "Parent Fixture";

    private readonly WebApplication app;
    private readonly WebApplication impostor;
    private readonly ConcurrentQueue<RecordedRequest> requests = new();
    private readonly ConcurrentQueue<RecordedRequest> impostorRequests = new();
    private readonly ConcurrentDictionary<string, FakeItem> items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> renamed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> deleted = new(StringComparer.Ordinal);
    private readonly int[] closedPorts;

    private FakePlexServer(WebApplication app, Uri address, WebApplication impostor, Uri impostorAddress, int[] closedPorts)
    {
        this.app = app;
        this.Address = address;
        this.impostor = impostor;
        this.ImpostorAddress = impostorAddress;
        this.closedPorts = closedPorts;
        this.ResourcesJson = this.Render(Fixture("resources.json"));
        this.AddItem(new FakeItem("101", "movie", "plex://movie/5d7768ba96b655001fdc0408", "The Matrix", 1));
        this.AddItem(new FakeItem("102", "movie", "plex://movie/5d776b59ad5437001f79c6f8", "Spirited Away", 1));
        this.AddItem(new FakeItem("201", "show", "plex://show/5d9c086c46115600200aa2fe", "Bluey", 2));
        this.AddItem(new FakeItem("2011", "season", "plex://season/602e6a7e8f4d2a002c7b5e10", "Season 1", 2) { ParentRatingKey = "201" });
        this.AddItem(new FakeItem("20111", "episode", "plex://episode/5d9c11ae2df347001e3b1a52", "Magic Xylophone", 2)
        {
            ParentRatingKey = "2011",
            GrandparentRatingKey = "201",
        });
        this.AddItem(new FakeItem("501", "artist", "plex://artist/5d07bbfc403c6402904a5ec9", "The Wiggles", 5));
    }

    public Uri Address { get; }

    public Uri ImpostorAddress { get; }

    public Uri LocalHttpsAddress => Closed(this.closedPorts[0], "https");

    public Uri RemoteHttpsAddress => Closed(this.closedPorts[1], "https");

    public Uri RelayAddress => Closed(this.closedPorts[2], "https");

    public Uri RemoteHttpAddress => Closed(this.closedPorts[3], "http");

    public string ResourcesJson { get; set; }

    public ConcurrentDictionary<string, bool> HiddenKeys { get; } = new(StringComparer.Ordinal);

    public bool PinApproved { get; set; }

    public bool PinGone { get; set; }

    public string? UsersXml { get; set; }

    public int? UsersStatus { get; set; }

    public string? ServerTokenOverride { get; set; }

    public IReadOnlyList<RecordedRequest> Requests => [.. this.requests];

    public IReadOnlyList<RecordedRequest> ImpostorRequests => [.. this.impostorRequests];

    public Action<RecordedRequest>? AfterRequest { get; set; }

    public Func<RecordedRequest, Task>? Delay { get; set; }

    public TimeSpan MetadataDelay { get; set; }

    public static async Task<FakePlexServer> StartAsync()
    {
        FakePlexServer? server = null;
        (WebApplication main, Uri mainAddress) = await StartAppAsync(context => server!.HandleAsync(context));
        (WebApplication impostor, Uri impostorAddress) = await StartAppAsync(context => server!.HandleImpostorAsync(context));
        server = new FakePlexServer(main, mainAddress, impostor, impostorAddress, [ClosedPort(), ClosedPort(), ClosedPort(), ClosedPort()]);
        return server;
    }

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Plex", "Fixtures", name));

    public int Count(string path) => this.requests.Count(request => request.Path == path);

    public int Count(string method, string path) => this.requests.Count(request => request.Method == method && request.Path == path);

    public FakeItem Item(string ratingKey) => this.items[ratingKey];

    // Plex gives an item that is removed and added again a new rating key, and labels set on the old item are gone. A new guid stands for the title being matched to different metadata.
    public FakeItem ReAddItem(string ratingKey, string newRatingKey, string? newGuid = null)
    {
        FakeItem old = this.items[ratingKey];
        this.items.TryRemove(ratingKey, out _);
        FakeItem added = new(newRatingKey, old.Type, newGuid ?? old.Guid, old.Title, old.Section);
        this.items[newRatingKey] = added;
        string original = this.renamed.FirstOrDefault(pair => pair.Value == ratingKey).Key ?? ratingKey;
        this.renamed[original] = newRatingKey;
        return added;
    }

    public void DeleteItem(string ratingKey)
    {
        this.items.TryRemove(ratingKey, out _);
        this.deleted[ratingKey] = true;
    }

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        await this.impostor.StopAsync();
        await this.impostor.DisposeAsync();
    }

    private static Uri Closed(int port, string scheme) => new($"{scheme}://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");

    private static int ClosedPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(WebApplication App, Uri Address)> StartAppAsync(RequestDelegate handler)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.Run(handler);
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new Uri(address.TrimEnd('/') + "/"));
    }

    private static RecordedRequest Record(HttpContext context) => new(
        context.Request.Method,
        context.Request.Path.Value ?? string.Empty,
        context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()),
        Header(context, "X-Plex-Token"),
        Header(context, "X-Plex-Client-Identifier"));

    private static string? Header(HttpContext context, string name) =>
        context.Request.Headers.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues value) ? value.ToString() : null;

    private static Task WriteAsync(HttpContext context, string body, string contentType = "application/json", int status = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        return context.Response.WriteAsync(body);
    }

    private string Page(string json, IQueryCollection query)
    {
        int start = int.TryParse(query["X-Plex-Container-Start"], CultureInfo.InvariantCulture, out int parsedStart) ? parsedStart : 0;
        int size = int.TryParse(query["X-Plex-Container-Size"], CultureInfo.InvariantCulture, out int parsedSize) ? parsedSize : int.MaxValue;
        long? updatedSince = long.TryParse(query["updatedAt>>"], CultureInfo.InvariantCulture, out long parsedSince) ? parsedSince : null;
        string? labelKey = query["label"].ToString() is { Length: > 0 } requested ? requested : null;
        string? guid = query["guid"].ToString() is { Length: > 0 } wanted ? wanted : null;
        JsonNode root = JsonNode.Parse(json)!;
        JsonObject container = root["MediaContainer"]!.AsObject();
        JsonArray items = container["Metadata"]!.AsArray();
        foreach (JsonNode? item in items)
        {
            string key = item!["ratingKey"]!.GetValue<string>();
            if (this.renamed.TryGetValue(key, out string? newKey))
            {
                item["ratingKey"] = newKey;
                item["key"] = $"/library/metadata/{newKey}";
                if (this.items.TryGetValue(newKey, out FakeItem? current))
                {
                    item["guid"] = current.Guid;
                }
            }
        }

        List<JsonNode> matching = [.. items
            .Where(item => !this.deleted.ContainsKey(item!["ratingKey"]!.GetValue<string>()))
            .Where(item => guid is null || item!["guid"]?.GetValue<string>() == guid)
            .Where(item => updatedSince is null || (item!["updatedAt"]?.GetValue<long>() ?? 0) > updatedSince)
            .Where(item => labelKey is null || this.HasLabel(item!["ratingKey"]!.GetValue<string>(), labelKey))
            .Select(item => item!)];
        JsonArray page = [.. matching
            .Where(item => !this.HiddenKeys.ContainsKey(item["ratingKey"]!.GetValue<string>()))
            .Skip(start)
            .Take(size)
            .Select(item => item.DeepClone())];
        container["Metadata"] = page;
        container["size"] = page.Count;
        container["totalSize"] = matching.Count;
        container["offset"] = start;
        return root.ToJsonString();
    }

    private string KeyOf(string tag) =>
        (1000 + this.items.Values.SelectMany(item => item.Labels).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList()
            .FindIndex(label => string.Equals(label, tag, StringComparison.OrdinalIgnoreCase))).ToString(CultureInfo.InvariantCulture);

    private bool HasLabel(string ratingKey, string labelKey) =>
        this.items.TryGetValue(ratingKey, out FakeItem? fake) && fake.Labels.Any(label => this.KeyOf(label) == labelKey);

    private string RenderLabels(int section)
    {
        JsonArray directories = [.. this.items.Values
            .Where(item => item.Section == section)
            .SelectMany(item => item.Labels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(label => (JsonNode)new JsonObject { ["fastKey"] = $"/library/sections/{section}/all?label={this.KeyOf(label)}", ["key"] = this.KeyOf(label), ["title"] = label })];
        return new JsonObject
        {
            ["MediaContainer"] = new JsonObject { ["size"] = directories.Count, ["title1"] = "Label", ["Directory"] = directories },
        }.ToJsonString();
    }

    private string Render(string template) => template
        .Replace("{{MACHINE}}", MachineIdentifier, StringComparison.Ordinal)
        .Replace("{{SERVER_TOKEN}}", ServerToken, StringComparison.Ordinal)
        .Replace("{{ACCOUNT_TOKEN}}", AccountToken, StringComparison.Ordinal)
        .Replace("{{LOCAL_HTTP}}", this.Address.ToString(), StringComparison.Ordinal)
        .Replace("{{IMPOSTOR}}", this.ImpostorAddress.ToString(), StringComparison.Ordinal)
        .Replace("{{LOCAL_HTTPS}}", this.LocalHttpsAddress.ToString(), StringComparison.Ordinal)
        .Replace("{{REMOTE_HTTPS}}", this.RemoteHttpsAddress.ToString(), StringComparison.Ordinal)
        .Replace("{{RELAY}}", this.RelayAddress.ToString(), StringComparison.Ordinal)
        .Replace("{{REMOTE_HTTP}}", this.RemoteHttpAddress.ToString(), StringComparison.Ordinal);

    private async Task HandleImpostorAsync(HttpContext context)
    {
        this.impostorRequests.Enqueue(Record(context));
        string body = Fixture("identity.json").Replace("{{MACHINE}}", ImpostorMachineIdentifier, StringComparison.Ordinal);
        await WriteAsync(context, body);
    }

    private async Task HandleAsync(HttpContext context)
    {
        RecordedRequest request = Record(context);
        this.requests.Enqueue(request);
        if (this.Delay is not null)
        {
            await this.Delay(request);
        }

        string path = request.Path;

        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await this.HandlePlexTvAsync(context, request);
            return;
        }

        if (path == "/identity")
        {
            await WriteAsync(context, this.Render(Fixture("identity.json")));
            return;
        }

        if (request.Token != (this.ServerTokenOverride ?? ServerToken))
        {
            await WriteAsync(context, "<html><head><title>Unauthorized</title></head><body><h1>401 Unauthorized</h1></body></html>", "text/html", StatusCodes.Status401Unauthorized);
            return;
        }

        if (path.StartsWith("/library/metadata/", StringComparison.Ordinal) && request.Method == HttpMethods.Get)
        {
            await this.HandleMetadataAsync(context, path["/library/metadata/".Length..]);
            this.AfterRequest?.Invoke(request);
            return;
        }

        if (request.Method == HttpMethods.Put && path.StartsWith("/library/sections/", StringComparison.Ordinal) && path.EndsWith("/all", StringComparison.Ordinal))
        {
            this.HandleEdit(context, path["/library/sections/".Length..^"/all".Length], request.Query);
            this.AfterRequest?.Invoke(request);
            return;
        }

        Match labelRoute = LabelRoute().Match(path);
        if (labelRoute.Success && request.Method == HttpMethods.Get)
        {
            await WriteAsync(context, this.RenderLabels(int.Parse(labelRoute.Groups[1].Value, CultureInfo.InvariantCulture)));
            this.AfterRequest?.Invoke(request);
            return;
        }

        string? fixture = path switch
        {
            "/" => "root.json",
            "/library/sections" => "sections.json",
            "/library/sections/1/all" => "section-1-all.json",
            "/library/sections/2/all" => "section-2-all.json",
            "/library/sections/3/all" => "section-3-all.json",
            "/library/sections/4/all" => "section-4-all.json",
            _ => null,
        };

        if (fixture is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string body = this.Render(Fixture(fixture));
        await WriteAsync(context, path.EndsWith("/all", StringComparison.Ordinal) ? this.Page(body, context.Request.Query) : body);
    }

    private void AddItem(FakeItem item) => this.items[item.RatingKey] = item;

    private async Task HandleMetadataAsync(HttpContext context, string ratingKey)
    {
        if (this.MetadataDelay > TimeSpan.Zero)
        {
            await Task.Delay(this.MetadataDelay);
        }

        if (!this.items.TryGetValue(ratingKey, out FakeItem? item))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        JsonObject metadata = new()
        {
            ["ratingKey"] = item.RatingKey,
            ["key"] = $"/library/metadata/{item.RatingKey}",
            ["guid"] = item.Guid,
            ["type"] = item.Type,
            ["title"] = item.Title,
            ["librarySectionID"] = item.Section,
        };
        if (item.ParentRatingKey is not null)
        {
            metadata["parentRatingKey"] = item.ParentRatingKey;
        }

        if (item.GrandparentRatingKey is not null)
        {
            metadata["grandparentRatingKey"] = item.GrandparentRatingKey;
        }

        (IReadOnlyList<string> labels, bool locked) = item.Snapshot();
        if (labels.Count > 0)
        {
            metadata["Label"] = new JsonArray([.. labels.Select((label, index) => (JsonNode)new JsonObject
            {
                ["id"] = 9000 + index,
                ["filter"] = $"label={9000 + index}",
                ["tag"] = label,
            })]);
        }

        if (locked)
        {
            metadata["Field"] = new JsonArray(new JsonObject { ["locked"] = true, ["name"] = "label" });
        }

        JsonObject root = new()
        {
            ["MediaContainer"] = new JsonObject
            {
                ["size"] = 1,
                ["librarySectionID"] = item.Section,
                ["Metadata"] = new JsonArray(metadata),
            },
        };
        await WriteAsync(context, root.ToJsonString());
    }

    private void HandleEdit(HttpContext context, string section, IReadOnlyDictionary<string, string> query)
    {
        string expectedType = query.GetValueOrDefault("type") switch
        {
            "1" => "movie",
            "2" => "show",
            _ => string.Empty,
        };
        if (!this.items.TryGetValue(query.GetValueOrDefault("id") ?? string.Empty, out FakeItem? item)
            || item.Section.ToString(CultureInfo.InvariantCulture) != section
            || item.Type != expectedType)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        List<string> replacement =
        [
            .. query
                .Select(pair => (Match: LabelKey().Match(pair.Key), pair.Value))
                .Where(entry => entry.Match.Success)
                .OrderBy(entry => int.Parse(entry.Match.Groups[1].Value, CultureInfo.InvariantCulture))
                .Select(entry => entry.Value),
        ];
        string[] removed = query.TryGetValue("label[].tag.tag-", out string? removal) ? removal.Split(',') : [];
        item.Edit(replacement.Count > 0 ? replacement : null, removed, query.GetValueOrDefault("label.locked") == "1");
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private async Task HandlePlexTvAsync(HttpContext context, RecordedRequest request)
    {
        if (string.IsNullOrEmpty(request.ClientIdentifier))
        {
            await WriteAsync(context, """{"errors":[{"code":1000,"message":"X-Plex-Client-Identifier is missing","status":400}]}""", status: StatusCodes.Status400BadRequest);
            return;
        }

        string pinPath = $"/api/v2/pins/{PinId.ToString(CultureInfo.InvariantCulture)}";
        if (request.Method == HttpMethods.Post && request.Path == "/api/v2/pins")
        {
            await WriteAsync(context, this.RenderPin(request.ClientIdentifier, approved: false), status: StatusCodes.Status201Created);
            return;
        }

        if (request.Method == HttpMethods.Get && request.Path == pinPath)
        {
            if (this.PinGone)
            {
                await WriteAsync(context, """{"errors":[{"code":1020,"message":"Code not found or expired","status":404}]}""", status: StatusCodes.Status404NotFound);
                return;
            }

            await WriteAsync(context, this.RenderPin(request.ClientIdentifier, this.PinApproved));
            return;
        }

        if (request.Token != AccountToken)
        {
            await WriteAsync(context, """{"errors":[{"code":1001,"message":"User could not be authenticated","status":401}]}""", status: StatusCodes.Status401Unauthorized);
            return;
        }

        switch (request.Path)
        {
            case "/api/v2/user":
                await WriteAsync(context, this.Render(Fixture("user.json")));
                break;
            case "/api/v2/resources":
                await WriteAsync(context, this.ResourcesJson);
                break;
            case "/api/users":
                await WriteAsync(context, this.UsersXml ?? Fixture("users.xml"), "application/xml", this.UsersStatus ?? StatusCodes.Status200OK);
                break;
            case "/api/v2/friends":
                context.Response.StatusCode = StatusCodes.Status410Gone;
                break;
            case "/api/home/users":
                await WriteAsync(context, Fixture("home-users.xml"), "application/xml");
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    private string RenderPin(string clientIdentifier, bool approved)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return Fixture("pin.json")
            .Replace("{{CLIENT}}", clientIdentifier, StringComparison.Ordinal)
            .Replace("{{CREATED}}", now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{EXPIRES}}", now.AddMinutes(30).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{AUTH_TOKEN}}", approved ? $"\"{AccountToken}\"" : "null", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^label\[(\d+)\]\.tag\.tag$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelKey();

    [GeneratedRegex(@"^/library/sections/([0-9]+)/label$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRoute();

    public sealed record RecordedRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Query,
        string? Token,
        string? ClientIdentifier);

    public sealed class FakeItem(string ratingKey, string type, string guid, string title, int section)
    {
        private readonly List<string> labels = [];
        private bool locked;

        public string RatingKey { get; } = ratingKey;

        public string Type { get; } = type;

        public string Guid { get; } = guid;

        public string Title { get; } = title;

        public int Section { get; } = section;

        public string? ParentRatingKey { get; init; }

        public string? GrandparentRatingKey { get; init; }

        public IReadOnlyList<string> Labels => this.Snapshot().Labels;

        public bool Locked => this.Snapshot().Locked;

        public (IReadOnlyList<string> Labels, bool Locked) Snapshot()
        {
            lock (this.labels)
            {
                return ([.. this.labels], this.locked);
            }
        }

        public void SetLabels(params string[] values)
        {
            lock (this.labels)
            {
                this.labels.Clear();
                this.labels.AddRange(values);
            }
        }

        public void AddLabel(string value)
        {
            lock (this.labels)
            {
                this.labels.Add(value);
            }
        }

        public void Edit(IReadOnlyList<string>? replacement, IReadOnlyList<string> removed, bool lockField)
        {
            lock (this.labels)
            {
                if (replacement is not null)
                {
                    this.labels.Clear();
                    this.labels.AddRange(replacement.Distinct(StringComparer.OrdinalIgnoreCase));
                }

                this.labels.RemoveAll(label => removed.Contains(label, StringComparer.OrdinalIgnoreCase));
                this.locked |= lockField;
            }
        }
    }
}
