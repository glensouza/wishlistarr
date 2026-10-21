using Wishlistarr.Api.Jobs;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Tmdb;

public interface ITmdbDataStore
{
    Type EntityType { get; }

    Task RefreshAsync(DateTimeOffset staleBefore, CancellationToken ct);

    Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, CancellationToken ct);

    Task<int> PurgeAllAsync(CancellationToken ct);
}

public sealed partial class TmdbDataService(
    IEnumerable<ITmdbDataStore> stores,
    TmdbCache cache,
    TmdbRetention retention,
    TimeProvider time,
    ILogger<TmdbDataService> logger)
{
    public async Task MaintainAsync(CancellationToken ct)
    {
        TimeSpan keep = await retention.GetAsync(ct);
        DateTimeOffset now = time.GetUtcNow();
        foreach (ITmdbDataStore store in stores)
        {
            try
            {
                await store.RefreshAsync(now - (keep / 2), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException)
            {
                this.LogRefreshFailed(ex, store.EntityType.Name);
            }

            await store.PurgeExpiredAsync(now - keep, ct);
        }
    }

    public async Task<int> PurgeAllAsync(CancellationToken ct)
    {
        cache.Clear();
        int purged = 0;
        foreach (ITmdbDataStore store in stores)
        {
            purged += await store.PurgeAllAsync(ct);
        }

        return purged;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing stored TMDB data for {Entity} failed; records past the retention are purged regardless.")]
    private partial void LogRefreshFailed(Exception exception, string entity);
}

public sealed class TmdbDataPurgeJob(TmdbDataService data) : IBackgroundJob
{
    public const string JobName = "tmdb-data-purge";

    public Task RunAsync(CancellationToken ct) => data.MaintainAsync(ct);
}
