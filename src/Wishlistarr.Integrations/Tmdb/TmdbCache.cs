using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Wishlistarr.Integrations.Tmdb;

public sealed class TmdbCache : IDisposable
{
    private const long ImageLimit = 50 * 1024 * 1024;
    private const int ImageEntryLimit = 2000;
    private static readonly TimeSpan ImageLifetime = TimeSpan.FromDays(7);
    private readonly object imageGate = new();
    private readonly Dictionary<string, LinkedListNode<ImageEntry>> images = [];
    private readonly LinkedList<ImageEntry> imageOrder = new();
    private long imageBytes;
    private long imageGeneration;
    private readonly MemoryCache cache;
    private readonly TimeProvider time;
    private readonly ITimer cleanup;

    public TmdbCache(IOptions<TmdbOptions> options, TimeProvider time)
    {
        this.time = time;
        this.cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = options.Value.CacheMaxEntries,
            TrackStatistics = true,
        });
        this.cleanup = time.CreateTimer(_ => this.CleanupImages(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public long EntryCount => this.cache.GetCurrentStatistics()?.CurrentEntryCount ?? 0;

    public bool TryGet<T>(string key, out T? value) => this.cache.TryGetValue(key, out value);

    public void Set<T>(string key, T value, TimeSpan lifetime) =>
        this.cache.Set(key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });

    public bool TryGetImage(string key, out TmdbImageContent? image, out long generation)
    {
        lock (this.imageGate)
        {
            generation = this.imageGeneration;
            this.RemoveExpiredImages();
            if (!this.images.TryGetValue(key, out LinkedListNode<ImageEntry>? node))
            {
                image = null;
                return false;
            }

            this.imageOrder.Remove(node);
            this.imageOrder.AddLast(node);
            image = node.Value.Image;
            return true;
        }
    }

    public void SetImage(string key, TmdbImageContent image, long generation)
    {
        lock (this.imageGate)
        {
            if (generation != this.imageGeneration) return;
            this.RemoveExpiredImages();
            if (this.images.TryGetValue(key, out LinkedListNode<ImageEntry>? existing)) this.RemoveImage(existing);
            if (image.Bytes.Length == 0 || image.Bytes.LongLength > ImageLimit) return;
            while (this.images.Count >= ImageEntryLimit || this.imageBytes + image.Bytes.LongLength > ImageLimit) this.RemoveImage(this.imageOrder.First!);
            LinkedListNode<ImageEntry> node = this.imageOrder.AddLast(new ImageEntry(key, image, this.time.GetUtcNow() + ImageLifetime));
            this.images.Add(key, node);
            this.imageBytes += image.Bytes.LongLength;
        }
    }

    public void Clear()
    {
        this.cache.Clear();
        lock (this.imageGate)
        {
            this.images.Clear();
            this.imageOrder.Clear();
            this.imageBytes = 0;
            this.imageGeneration++;
        }
    }

    private void CleanupImages()
    {
        lock (this.imageGate) this.RemoveExpiredImages();
    }

    private void RemoveExpiredImages()
    {
        DateTimeOffset now = this.time.GetUtcNow();
        LinkedListNode<ImageEntry>? node = this.imageOrder.First;
        while (node is not null)
        {
            LinkedListNode<ImageEntry>? next = node.Next;
            if (node.Value.ExpiresAt <= now) this.RemoveImage(node);
            node = next;
        }
    }

    private void RemoveImage(LinkedListNode<ImageEntry> node)
    {
        this.images.Remove(node.Value.Key);
        this.imageOrder.Remove(node);
        this.imageBytes -= node.Value.Image.Bytes.LongLength;
    }

    private sealed record ImageEntry(string Key, TmdbImageContent Image, DateTimeOffset ExpiresAt);

    public void Dispose()
    {
        this.cleanup.Dispose();
        this.cache.Dispose();
    }
}
