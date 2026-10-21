using Wishlistarr.Api.Jobs;
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
    TimeProvider time,
    ILogger<TmdbDataService> logger)
{
    public async Task MaintainAsync(CancellationToken ct)
    {
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

public sealed record TmdbPurgeOutcome(bool Completed, int? Purged);

public sealed partial class TmdbPurgeRunner(IServiceScopeFactory scopes, ILogger<TmdbPurgeRunner> logger)
{
    public TimeSpan WaitLimit { get; set; } = TimeSpan.FromSeconds(5);

    public async Task<TmdbPurgeOutcome> PurgeAllAsync()
    {
        Task<int> purge = Task.Run(async () =>
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<TmdbDataService>().PurgeAllAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                this.LogFailed(ex);
                throw;
            }
        });
        if (await Task.WhenAny(purge, Task.Delay(this.WaitLimit)) == purge)
        {
            return new TmdbPurgeOutcome(true, await purge);
        }

        return new TmdbPurgeOutcome(false, null);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Purging all stored TMDB data failed.")]
    private partial void LogFailed(Exception exception);
}
