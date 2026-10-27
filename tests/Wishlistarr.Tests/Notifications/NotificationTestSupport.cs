using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Notifications;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Tests.Requests;

namespace Wishlistarr.Tests.Notifications;

public sealed class NotificationFactory : AuthApiFactory
{
    public ManualTimeProvider Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting($"Jobs:Schedules:{NotificationJob.JobName}:Enabled", "false");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(this.Clock);
            services.RemoveAll<ILibraryIndex>();
            services.AddSingleton<ILibraryIndex>(new FakeLibraryIndex());
        });
    }

    public async Task<int> RequestAsync(int tmdbId = 123)
    {
        using IServiceScope scope = this.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        KidProfile kid = new() { Name = $"Kid {tmdbId}", Avatar = "fox", PlexLabel = $"Kid{tmdbId}" };
        db.KidProfiles.Add(kid);
        await db.SaveChangesAsync();
        RequestResult result = await scope.ServiceProvider.GetRequiredService<MediaRequests>()
            .CreateAsync(kid.Id, new TitleKey(MediaKind.Movie, tmdbId), "Test @everyone title", RequestActor.Kid(kid.Id, kid.Name));
        return result.Request!.Id;
    }

    public async Task RunAsync()
    {
        JobStartResult run = await this.Services.GetRequiredService<JobRunner>().StartAsync(NotificationJob.JobName, JobRunTrigger.Manual);
        Assert.Equal(JobStartStatus.Started, run.Status);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        JobRun? result = await this.Services.GetRequiredService<JobRunner>().GetLastRunAsync(NotificationJob.JobName);
        Assert.Equal(JobRunOutcome.Succeeded, result?.Outcome);
    }
}

public sealed record NotificationHttpRequest(string Path, string Query, string Authorization, string ContentType, string Body);

public sealed class NotificationHttpServer : IAsyncDisposable
{
    private readonly WebApplication app;

    private NotificationHttpServer(WebApplication app, Uri address)
    {
        this.app = app;
        this.Address = address;
    }

    public Uri Address { get; }
    public ConcurrentQueue<NotificationHttpRequest> Requests { get; } = new();
    public int Status { get; set; } = 200;
    public string? Redirect { get; set; }
    public bool AppriseProtocol { get; set; }
    public bool AppriseDefaultTargets { get; set; }
    public bool AppriseValidatesUnusedUrls { get; set; }

    public static async Task<NotificationHttpServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        NotificationHttpServer? server = null;
        app.MapPost("/{**path}", async context =>
        {
            string body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            server!.Requests.Enqueue(new NotificationHttpRequest(context.Request.Path, context.Request.QueryString.Value ?? string.Empty,
                context.Request.Headers.Authorization.ToString(), context.Request.ContentType ?? string.Empty, body));
            int protocolStatus = server.AppriseProtocol ? server.AppriseStatus(context.Request.Path, body) : 200;
            context.Response.StatusCode = protocolStatus == 200 ? server.Status : protocolStatus;
            if (server.Redirect is not null)
            {
                context.Response.Headers.Location = server.Redirect;
            }

            await context.Response.WriteAsync(context.Response.StatusCode == 200 ? "{\"success\":true}" : "upstream private error");
        });
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        server = new NotificationHttpServer(app, new Uri(address));
        return server;
    }

    public async ValueTask DisposeAsync() => await this.app.DisposeAsync();

    private int AppriseStatus(string path, string body)
    {
        string endpoint = path.StartsWith("/prefix/", StringComparison.Ordinal) ? path[7..] : path;
        bool stateful = endpoint.StartsWith("/notify/", StringComparison.Ordinal) && endpoint.TrimEnd('/').Length > "/notify".Length;
        if (!stateful && endpoint is not ("/notify" or "/notify/"))
        {
            return 404;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("body", out JsonElement message) || message.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(message.GetString()))
            {
                return 400;
            }

            bool hasUrls = json.RootElement.TryGetProperty("urls", out JsonElement urls);
            if (stateful && !this.AppriseValidatesUnusedUrls)
            {
                return 200;
            }

            if (hasUrls && urls.ValueKind != JsonValueKind.String
                && (urls.ValueKind != JsonValueKind.Array || urls.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)))
            {
                return 400;
            }

            bool targets = hasUrls && (urls.ValueKind == JsonValueKind.String
                ? !string.IsNullOrWhiteSpace(urls.GetString())
                : urls.EnumerateArray().Any(value => !string.IsNullOrWhiteSpace(value.GetString())));
            return stateful || targets || this.AppriseDefaultTargets ? 200 : 400;
        }
        catch (JsonException)
        {
            return 400;
        }
    }
}

public sealed class NotificationSmtpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task receive;
    private readonly TaskCompletionSource<string> message = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NotificationSmtpServer()
    {
        this.listener.Start();
        this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
        this.receive = this.ReceiveAsync();
    }

    public int Port { get; }
    public Task<string> Message => this.message.Task;

    private async Task ReceiveAsync()
    {
        try
        {
            using TcpClient client = await this.listener.AcceptTcpClientAsync(this.stopping.Token);
            using StreamReader reader = new(client.GetStream(), Encoding.ASCII);
            using StreamWriter writer = new(client.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost SMTP");
            while (await reader.ReadLineAsync(this.stopping.Token) is { } line)
            {
                if (line.StartsWith("EHLO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250-localhost\r\n250 8BITMIME");
                }
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 End with dot");
                    StringBuilder data = new();
                    while (await reader.ReadLineAsync(this.stopping.Token) is { } content && content != ".")
                    {
                        data.AppendLine(content);
                    }

                    this.message.TrySetResult(data.ToString());
                    await writer.WriteLineAsync("250 queued");
                }
                else if (line == "QUIT")
                {
                    await writer.WriteLineAsync("221 bye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("250 ok");
                }
            }
        }
        catch (OperationCanceledException) when (this.stopping.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.stopping.CancelAsync();
        this.listener.Stop();
        await this.receive;
        this.stopping.Dispose();
    }
}
