using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wishlistarr.Tests.Plex;

public sealed class FakePlexServer : IAsyncDisposable
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
    private readonly int[] closedPorts;

    private FakePlexServer(WebApplication app, Uri address, WebApplication impostor, Uri impostorAddress, int[] closedPorts)
    {
        this.app = app;
        this.Address = address;
        this.impostor = impostor;
        this.ImpostorAddress = impostorAddress;
        this.closedPorts = closedPorts;
        this.ResourcesJson = this.Render(Fixture("resources.json"));
    }

    public Uri Address { get; }

    public Uri ImpostorAddress { get; }

    public Uri LocalHttpsAddress => Closed(this.closedPorts[0], "https");

    public Uri RemoteHttpsAddress => Closed(this.closedPorts[1], "https");

    public Uri RelayAddress => Closed(this.closedPorts[2], "https");

    public Uri RemoteHttpAddress => Closed(this.closedPorts[3], "http");

    public string ResourcesJson { get; set; }

    public bool PinApproved { get; set; }

    public bool PinGone { get; set; }

    public string? ServerTokenOverride { get; set; }

    public IReadOnlyList<RecordedRequest> Requests => [.. this.requests];

    public IReadOnlyList<RecordedRequest> ImpostorRequests => [.. this.impostorRequests];

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

    private static string Page(string json, IQueryCollection query)
    {
        int start = int.TryParse(query["X-Plex-Container-Start"], CultureInfo.InvariantCulture, out int parsedStart) ? parsedStart : 0;
        int size = int.TryParse(query["X-Plex-Container-Size"], CultureInfo.InvariantCulture, out int parsedSize) ? parsedSize : int.MaxValue;
        JsonNode root = JsonNode.Parse(json)!;
        JsonObject container = root["MediaContainer"]!.AsObject();
        JsonArray items = container["Metadata"]!.AsArray();
        JsonArray page = [.. items.Skip(start).Take(size).Select(item => item!.DeepClone())];
        container["Metadata"] = page;
        container["size"] = page.Count;
        container["offset"] = start;
        return root.ToJsonString();
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
        await WriteAsync(context, path.EndsWith("/all", StringComparison.Ordinal) ? Page(body, context.Request.Query) : body);
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
            case "/api/v2/friends":
                await WriteAsync(context, Fixture("friends.json"));
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

    public sealed record RecordedRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Query,
        string? Token,
        string? ClientIdentifier);
}
