using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.Settings;
using Wishlistarr.Integrations.Arr;
using Wishlistarr.Tests.Plex;

namespace Wishlistarr.Tests.Arr;

public sealed class ArrHarness : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private ArrHarness(FakeArrServer server, InMemoryAppSettings settings)
    {
        this.Server = server;
        this.Settings = settings;
        ServiceCollection services = new();
        services.AddSingleton<IAppSettings>(settings);
        services.AddArr<InMemoryAppSettings>();
        this.provider = services.BuildServiceProvider();
    }

    public FakeArrServer Server { get; }

    public InMemoryAppSettings Settings { get; }

    public ArrConnectionStore Store => this.provider.GetRequiredService<ArrConnectionStore>();

    public IArrBackend Backend => this.provider.GetRequiredService<ArrBackends>().Get(this.Server.Service);

    public static async Task<ArrHarness> CreateAsync(ArrService service, bool configured = true)
    {
        ArrHarness harness = new(await FakeArrServer.StartAsync(service), new InMemoryAppSettings());
        if (configured)
        {
            await harness.SaveAsync(FakeArrServer.ApiKey, harness.Server.Address);
        }

        return harness;
    }

    public Task SaveAsync(string apiKey, Uri url, int? profile = 4, string? root = "/media/kids", params int[] tagIds) =>
        this.Store.SaveAsync(this.Server.Service, url, apiKey, profile, root, tagIds);

    public async ValueTask DisposeAsync()
    {
        await this.provider.DisposeAsync();
        await this.Server.DisposeAsync();
    }
}
