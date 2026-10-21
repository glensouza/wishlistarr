using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.Settings;
using Wishlistarr.Integrations.Seerr;
using Wishlistarr.Tests.Plex;

namespace Wishlistarr.Tests.Seerr;

public sealed class RecordingSink : IAsyncDisposable
{
    private readonly WebApplication app;

    private RecordingSink(WebApplication app, Uri address)
    {
        this.app = app;
        this.Address = address;
    }

    public Uri Address { get; }

    public int Hits { get; private set; }

    public static async Task<RecordingSink> StartAsync()
    {
        RecordingSink? sink = null;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.Run(context =>
        {
            sink!.Hits++;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        sink = new RecordingSink(app, new Uri(address.TrimEnd('/') + "/"));
        return sink;
    }

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
    }
}

public sealed class SeerrHarness : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private SeerrHarness(FakeSeerrServer server, InMemoryAppSettings settings)
    {
        this.Server = server;
        this.Settings = settings;
        ServiceCollection services = new();
        services.AddSingleton<IAppSettings>(settings);
        services.AddSeerr<InMemoryAppSettings>();
        this.provider = services.BuildServiceProvider();
    }

    public FakeSeerrServer Server { get; }

    public InMemoryAppSettings Settings { get; }

    public ISeerrBackend Backend => this.provider.GetRequiredService<ISeerrBackend>();

    public static async Task<SeerrHarness> CreateAsync(bool configured = true, int userId = FakeSeerrServer.DefaultUserId)
    {
        SeerrHarness harness = new(await FakeSeerrServer.StartAsync(), new InMemoryAppSettings());
        if (configured)
        {
            await harness.SaveAsync(FakeSeerrServer.ApiKey, harness.Server.Address, userId);
        }

        return harness;
    }

    public Task SaveAsync(string apiKey, Uri url, int userId = FakeSeerrServer.DefaultUserId) =>
        this.provider.GetRequiredService<SeerrConnectionStore>().SaveAsync(url, apiKey, userId);

    public async ValueTask DisposeAsync()
    {
        await this.provider.DisposeAsync();
        await this.Server.DisposeAsync();
    }
}
