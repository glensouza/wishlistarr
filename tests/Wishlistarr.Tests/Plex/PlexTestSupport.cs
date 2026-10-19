using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Settings;
using Wishlistarr.Integrations.Plex;

namespace Wishlistarr.Tests.Plex;

public sealed class InMemoryAppSettings : IAppSettings
{
    private readonly ConcurrentDictionary<string, StoredValue> values = new();

    public IReadOnlyDictionary<string, StoredValue> Values => this.values;

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(this.values.TryGetValue(key, out StoredValue? value) ? value.Value : null);

    public Task SetAsync(string key, string value, bool isSecret, CancellationToken ct = default)
    {
        this.values[key] = new StoredValue(value, isSecret);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        this.values.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public sealed record StoredValue(string Value, bool IsSecret);
}

public sealed class PlexHarness : IAsyncDisposable
{
    private readonly List<ServiceProvider> providers = [];
    private readonly Action<PlexOptions>? configure;

    private PlexHarness(FakePlexServer server, Action<PlexOptions>? configure)
    {
        this.Server = server;
        this.configure = configure;
        this.Provider = this.CreateProvider();
    }

    public FakePlexServer Server { get; }

    public InMemoryAppSettings Settings { get; } = new();

    public ServiceProvider Provider { get; }

    public IMediaServer Media => this.Provider.GetRequiredService<IMediaServer>();

    public IMediaServerAccount Account => this.Provider.GetRequiredService<IMediaServerAccount>();

    public static async Task<PlexHarness> CreateAsync(Action<PlexOptions>? configure = null) =>
        new(await FakePlexServer.StartAsync(), configure);

    public ServiceProvider CreateProvider()
    {
        ServiceCollection services = new();
        services.AddPlex<InMemoryAppSettings>();
        services.AddSingleton<IAppSettings>(this.Settings);
        services.Configure<PlexOptions>(options =>
        {
            options.PlexTvBaseAddress = this.Server.Address;
            options.ProbeTimeout = TimeSpan.FromSeconds(3);
            this.configure?.Invoke(options);
        });
        ServiceProvider provider = services.BuildServiceProvider();
        this.providers.Add(provider);
        return provider;
    }

    public async Task LinkAsync()
    {
        MediaServerLinkStart link = await this.Account.StartLinkAsync();
        this.Server.PinApproved = true;
        MediaServerLinkCheck? check = await this.Account.CheckLinkAsync(link.Id);
        Assert.Equal(MediaServerLinkState.Linked, check?.State);
    }

    public async Task<SelectedMediaServer> LinkAndSelectAsync()
    {
        await this.LinkAsync();
        SelectedMediaServer? selected = await this.Account.SelectServerAsync(FakePlexServer.MachineIdentifier);
        Assert.NotNull(selected);
        return selected;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ServiceProvider provider in this.providers)
        {
            await provider.DisposeAsync();
        }

        await this.Server.DisposeAsync();
    }
}
