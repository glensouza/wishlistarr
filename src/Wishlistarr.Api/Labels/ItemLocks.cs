namespace Wishlistarr.Api.Labels;

public sealed class ItemLocks
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        Entry? entry;
        lock (this.entries)
        {
            if (!this.entries.TryGetValue(key, out entry))
            {
                entry = new Entry();
                this.entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch
        {
            this.Leave(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Leave(string key, Entry entry)
    {
        lock (this.entries)
        {
            if (--entry.Users == 0)
            {
                this.entries.Remove(key);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Releaser(ItemLocks owner, string key, Entry entry) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.released, 1) == 0)
            {
                entry.Gate.Release();
                owner.Leave(key, entry);
            }
        }
    }
}
