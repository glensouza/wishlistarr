using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Settings;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Tmdb;

public interface ITmdbDataStore
{
    Type EntityType { get; }

    bool WaitsForLibraryRefresh { get; }

    Task RefreshAsync(DateTimeOffset staleBefore, CancellationToken ct);

    Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, CancellationToken ct);

    Task<int> PurgeAllAsync(CancellationToken ct);
}

public sealed partial class TmdbDataService(
    IEnumerable<ITmdbDataStore> stores,
    TmdbCache cache,
    TmdbRetention retention,
    ITmdbApiKeyProvider keys,
    SettingsStore settings,
    TimeProvider time,
    ILogger<TmdbDataService> logger)
{
    public const string PurgePendingSetting = SettingsStore.ReservedPrefix + "tmdb_purge_pending";

    public async Task MaintainAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(await keys.GetApiKeyAsync(ct)) || await this.IsPurgePendingAsync(ct))
        {
            await this.PurgeAllAsync(ct);
            await this.ClearPurgePendingAsync(ct);
            return;
        }

        TimeSpan keep = await retention.GetAsync(ct);
        DateTimeOffset now = time.GetUtcNow();
        List<Exception> failures = [];
        foreach (ITmdbDataStore store in stores)
        {
            try
            {
                await store.RefreshAsync(now - (keep / 2), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                this.LogRefreshFailed(ex, store.EntityType.Name);
            }

            try
            {
                await store.PurgeExpiredAsync(now - keep, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                this.LogPurgeFailed(ex, store.EntityType.Name);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Purging stored TMDB data failed.", failures);
        }
    }

    public async Task<int> PurgeAllAsync(CancellationToken ct)
    {
        cache.Clear();
        int purged = 0;
        List<Exception> failures = [];
        foreach (ITmdbDataStore store in stores.OrderBy(store => store.WaitsForLibraryRefresh))
        {
            try
            {
                purged += await store.PurgeAllAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                this.LogPurgeFailed(ex, store.EntityType.Name);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Purging stored TMDB data failed.", failures);
        }

        return purged;
    }

    public async Task<bool> IsPurgePendingAsync(CancellationToken ct) =>
        await settings.GetAsync(PurgePendingSetting, ct) == "true";

    public Task MarkPurgePendingAsync(CancellationToken ct) => settings.SetAsync(PurgePendingSetting, "true", false, ct);

    public Task ClearPurgePendingAsync(CancellationToken ct) => settings.DeleteAsync(PurgePendingSetting, ct);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing stored TMDB data for {Entity} failed; records past the retention are purged regardless.")]
    private partial void LogRefreshFailed(Exception exception, string entity);

    [LoggerMessage(Level = LogLevel.Error, Message = "Purging stored TMDB data for {Entity} failed.")]
    private partial void LogPurgeFailed(Exception exception, string entity);
}

public sealed class TmdbDataPurgeJob(TmdbDataService data) : IBackgroundJob
{
    public const string JobName = "tmdb-data-purge";

    public Task RunAsync(CancellationToken ct) => data.MaintainAsync(ct);
}
