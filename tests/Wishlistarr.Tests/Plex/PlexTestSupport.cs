using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Settings;
using Wishlistarr.Integrations.Plex;

namespace Wishlistarr.Tests.Plex;

public sealed class InMemoryAppSettings : IAppSettings
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, StoredValue> values = new();

    public IReadOnlyDictionary<string, StoredValue> Values => this.values;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await Task.Delay(1, ct);
        lock (this.gate)
        {
            return this.values.TryGetValue(key, out StoredValue? value) ? value.Value : null;
        }
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetAsync(IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        await Task.Delay(1, ct);
        lock (this.gate)
        {
            Dictionary<string, string?> result = [];
            foreach (string key in keys)
            {
                result[key] = this.values.TryGetValue(key, out StoredValue? value) ? value.Value : null;
            }

            return result;
        }
    }

    public async Task SetAsync(string key, string value, bool isSecret, CancellationToken ct = default)
    {
        await Task.Delay(1, ct);
        lock (this.gate)
        {
            this.values[key] = new StoredValue(value, isSecret);
        }
    }

    public async Task SetAsync(IReadOnlyList<AppSettingChange> changes, CancellationToken ct = default)
    {
        await Task.Delay(1, ct);
        lock (this.gate)
        {
            foreach (AppSettingChange change in changes)
            {
                this.values[change.Key] = new StoredValue(change.Value, change.IsSecret);
            }
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await Task.Delay(1, ct);
        lock (this.gate)
        {
            this.values.TryRemove(key, out _);
        }
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
