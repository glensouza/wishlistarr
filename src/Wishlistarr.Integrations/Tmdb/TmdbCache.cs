using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Wishlistarr.Integrations.Tmdb;

public sealed class TmdbCache(IOptions<TmdbOptions> options) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions
    {
        SizeLimit = options.Value.CacheMaxEntries,
        TrackStatistics = true,
    });

    public long EntryCount => this.cache.GetCurrentStatistics()?.CurrentEntryCount ?? 0;

    public bool TryGet<T>(string key, out T? value) => this.cache.TryGetValue(key, out value);

    public void Set<T>(string key, T value, TimeSpan lifetime) =>
        this.cache.Set(key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });

    public void Clear() => this.cache.Clear();

    public void Dispose() => this.cache.Dispose();
}
