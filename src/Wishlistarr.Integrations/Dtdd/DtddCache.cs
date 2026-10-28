using Microsoft.Extensions.Options;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Integrations.Dtdd;

public sealed class DtddCache(TimeProvider time, IOptions<DtddOptions> options)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public bool TryGet(MediaKind kind, int tmdbId, out IReadOnlyList<string>? names)
    {
        string key = Key(kind, tmdbId);
        lock (this.gate)
        {
            if (!this.entries.TryGetValue(key, out Entry? entry))
            {
                names = null;
                return false;
            }

            if (time.GetUtcNow() >= entry.ExpiresAt)
            {
                this.entries.Remove(key);
                names = null;
                return false;
            }

            names = entry.Names;
            return true;
        }
    }

    public void Set(MediaKind kind, int tmdbId, IReadOnlyList<string> names)
    {
        DateTimeOffset expiresAt = time.GetUtcNow() + options.Value.CacheLifetime;
        lock (this.gate)
        {
            this.entries[Key(kind, tmdbId)] = new Entry(names, expiresAt);
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.entries.Clear();
        }
    }

    private static string Key(MediaKind kind, int tmdbId) => $"{kind}:{tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private sealed record Entry(IReadOnlyList<string> Names, DateTimeOffset ExpiresAt);
}
