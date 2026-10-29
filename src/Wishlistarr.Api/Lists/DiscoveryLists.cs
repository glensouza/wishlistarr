using System.Text.Json;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core.Settings;
using Wishlistarr.Integrations.Lists;

namespace Wishlistarr.Api.Lists;

public sealed record DiscoveryList(string Provider, long ListId, string Name);
public sealed record DiscoveryListSettings(IReadOnlyList<DiscoveryList> Lists, string? TraktKey, string? MdbListKey);
public sealed record DiscoveryListView(IReadOnlyList<DiscoveryList> Lists, bool HasTraktKey, bool HasMdbListKey);
public sealed record DiscoveryListSnapshot(DiscoveryList Source, IReadOnlyList<ListItem> Items);

public sealed class DiscoveryListGate
{
    public SemaphoreSlim Mutex { get; } = new(1, 1);
    public long Version { get; set; }
    public Dictionary<string, DateTimeOffset> Cooldowns { get; } = [];
}

public sealed class DiscoveryLists(SettingsStore settings, IHttpClientFactory clients, DiscoveryListGate gate, TimeProvider time) : IBackgroundJob
{
    public const string JobName = "discovery-lists";
    private const string ConfigKey = "system.discovery-lists.config";
    private const string SnapshotKey = "system.discovery-lists.snapshot";
    private const string TraktKey = "system.discovery-lists.trakt-key";
    private const string MdbKey = "system.discovery-lists.mdblist-key";
    private const string ClientName = "discovery-lists";

    public async Task<DiscoveryListView> GetAsync(CancellationToken ct)
    {
        await gate.Mutex.WaitAsync(ct);
        try
        {
            return new DiscoveryListView(
                await this.SourcesAsync(ct),
                !string.IsNullOrWhiteSpace(await settings.GetAsync(TraktKey, ct)),
                !string.IsNullOrWhiteSpace(await settings.GetAsync(MdbKey, ct)));
        }
        finally
        {
            gate.Mutex.Release();
        }
    }

    public async Task<bool> SaveAsync(DiscoveryListSettings input, CancellationToken ct)
    {
        if (input.Lists is null || input.Lists.Count > 5 || !ValidKey(input.TraktKey) || !ValidKey(input.MdbListKey))
        {
            return false;
        }
        List<DiscoveryList> sources = [];
        foreach (DiscoveryList? source in input.Lists)
        {
            if (source is null || source.Provider is not ("trakt" or "mdblist" or "mdblist-external") || source.ListId <= 0
                || string.IsNullOrWhiteSpace(source.Name) || source.Name.Trim().Length > 80
                || sources.Any(item => item.Provider == source.Provider && item.ListId == source.ListId))
            {
                return false;
            }
            sources.Add(source with { Name = source.Name.Trim() });
        }
        await gate.Mutex.WaitAsync(ct);
        try
        {
            IReadOnlyList<DiscoveryListSnapshot> existing = await this.ReadSnapshotsAsync(ct);
            List<DiscoveryListSnapshot> retained = AlignSnapshots(sources, existing);
            List<AppSettingChange> changes = [new(ConfigKey, JsonSerializer.Serialize(sources), false), new(SnapshotKey, JsonSerializer.Serialize(retained), false)];
            if (!string.IsNullOrWhiteSpace(input.TraktKey))
            {
                changes.Add(new(TraktKey, input.TraktKey.Trim(), true));
            }
            if (!string.IsNullOrWhiteSpace(input.MdbListKey))
            {
                changes.Add(new(MdbKey, input.MdbListKey.Trim(), true));
            }
            await settings.SetAsync(changes, ct);
            gate.Version++;
            return true;
        }
        finally
        {
            gate.Mutex.Release();
        }
    }

    public async Task<IReadOnlyList<DiscoveryListSnapshot>> SnapshotsAsync(CancellationToken ct)
    {
        await gate.Mutex.WaitAsync(ct);
        try
        {
            return await this.ReadSnapshotsAsync(ct);
        }
        finally
        {
            gate.Mutex.Release();
        }
    }

    private async Task<IReadOnlyList<DiscoveryListSnapshot>> ReadSnapshotsAsync(CancellationToken ct)
    {
        string? raw = await settings.GetAsync(SnapshotKey, ct);
        IReadOnlyList<DiscoveryList> sources = await this.SourcesAsync(ct);
        List<DiscoveryListSnapshot> snapshots = raw is null ? [] : JsonSerializer.Deserialize<List<DiscoveryListSnapshot>>(raw) ?? [];
        return AlignSnapshots(sources, snapshots);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        IReadOnlyList<DiscoveryList> sources;
        string? trakt;
        string? mdb;
        IReadOnlyList<DiscoveryListSnapshot> previous;
        long version;
        await gate.Mutex.WaitAsync(ct);
        try
        {
            sources = await this.SourcesAsync(ct);
            trakt = await settings.GetAsync(TraktKey, ct);
            mdb = await settings.GetAsync(MdbKey, ct);
            previous = await this.ReadSnapshotsAsync(ct);
            version = ++gate.Version;
        }
        finally
        {
            gate.Mutex.Release();
        }
        List<DiscoveryListSnapshot> snapshots = [];
        bool failed = false;
        using HttpClient client = clients.CreateClient(ClientName);
        foreach (DiscoveryList source in sources)
        {
            string? key = source.Provider == "trakt" ? trakt : mdb;
            string provider = source.Provider == "trakt" ? "trakt" : "mdblist";
            bool cooling;
            lock (gate.Cooldowns)
            {
                cooling = gate.Cooldowns.TryGetValue(provider, out DateTimeOffset until) && until > time.GetUtcNow();
            }
            if (string.IsNullOrWhiteSpace(key) || cooling)
            {
                failed = true;
                if (previous.FirstOrDefault(item => SameSource(item.Source, source)) is DiscoveryListSnapshot old)
                {
                    snapshots.Add(old);
                }
                continue;
            }
            try
            {
                IReadOnlyList<ListItem> items = source.Provider == "trakt"
                    ? await ListItems.TraktAsync(client, source.ListId, key, ct)
                    : await ListItems.MdbListAsync(client, source.ListId, key, source.Provider == "mdblist-external", ct);
                snapshots.Add(new(source, items.Take(8).ToList()));
            }
            catch (ListRateLimitedException exception) when (!ct.IsCancellationRequested)
            {
                DateTimeOffset now = time.GetUtcNow();
                TimeSpan delay = exception.Delay ?? TimeSpan.FromMinutes(5);
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }
                DateTimeOffset until = exception.RetryAt ?? (delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay);
                lock (gate.Cooldowns)
                {
                    gate.Cooldowns[provider] = until > now ? until : now;
                }
                failed = true;
                if (previous.FirstOrDefault(item => SameSource(item.Source, source)) is DiscoveryListSnapshot old)
                {
                    snapshots.Add(old);
                }
            }
            catch (Exception exception) when (!ct.IsCancellationRequested && exception is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
            {
                failed = true;
                DiscoveryListSnapshot? old = previous.FirstOrDefault(item => SameSource(item.Source, source));
                if (old is not null)
                {
                    snapshots.Add(old);
                }
            }
        }
        await gate.Mutex.WaitAsync(ct);
        try
        {
            // A save or newer refresh supersedes results fetched outside the gate.
            if (gate.Version == version)
            {
                await settings.SetAsync(SnapshotKey, JsonSerializer.Serialize(snapshots), false, ct);
            }
        }
        finally
        {
            gate.Mutex.Release();
        }
        if (failed)
        {
            throw new InvalidOperationException("One or more discovery lists could not be refreshed.");
        }
    }

    private async Task<IReadOnlyList<DiscoveryList>> SourcesAsync(CancellationToken ct)
    {
        string? raw = await settings.GetAsync(ConfigKey, ct);
        return raw is null ? [] : JsonSerializer.Deserialize<List<DiscoveryList>>(raw) ?? [];
    }

    private static bool ValidKey(string? key) => string.IsNullOrWhiteSpace(key)
        || (key.Trim().Length <= 200 && key.Trim().All(character => character is >= '!' and <= '~'));

    private static bool SameSource(DiscoveryList first, DiscoveryList second) => first.Provider == second.Provider && first.ListId == second.ListId;

    private static List<DiscoveryListSnapshot> AlignSnapshots(IReadOnlyList<DiscoveryList> sources, IReadOnlyList<DiscoveryListSnapshot> snapshots)
    {
        List<DiscoveryListSnapshot> aligned = [];
        foreach (DiscoveryList source in sources)
        {
            if (snapshots.FirstOrDefault(item => SameSource(item.Source, source)) is DiscoveryListSnapshot snapshot)
            {
                aligned.Add(new(source, snapshot.Items));
            }
        }
        return aligned;
    }
}
