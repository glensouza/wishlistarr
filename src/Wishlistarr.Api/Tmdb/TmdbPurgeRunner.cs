using System.Collections.Concurrent;

namespace Wishlistarr.Api.Tmdb;

public sealed record TmdbPurgeOutcome(bool Completed, int? Purged, bool Failed = false);

public sealed partial class TmdbPurgeRunner(IServiceScopeFactory scopes, ILogger<TmdbPurgeRunner> logger) : IDisposable
{
    private readonly ConcurrentDictionary<Task, byte> running = new();
    private readonly CancellationTokenSource stopping = new();

    public TimeSpan WaitLimit { get; set; } = TimeSpan.FromSeconds(5);

    public async Task<TmdbPurgeOutcome> PurgeAllAsync()
    {
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TmdbDataService>().MarkPurgePendingAsync(CancellationToken.None);
        }

        Task<int?> purge = this.Start();
        if (await Task.WhenAny(purge, Task.Delay(this.WaitLimit)) != purge)
        {
            return new TmdbPurgeOutcome(false, null);
        }

        return await purge is int purged ? new TmdbPurgeOutcome(true, purged) : new TmdbPurgeOutcome(true, null, true);
    }

    public async Task ResumeAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        if (await scope.ServiceProvider.GetRequiredService<TmdbDataService>().IsPurgePendingAsync(ct))
        {
            _ = this.Start();
        }
    }

    public async Task DrainAsync(TimeSpan limit)
    {
        Task all = Task.WhenAll(this.running.Keys);
        if (await Task.WhenAny(all, Task.Delay(limit)) != all)
        {
            await this.stopping.CancelAsync();
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(1)));
        }
    }

    public void Dispose() => this.stopping.Dispose();

    private Task<int?> Start()
    {
        TaskCompletionSource<int?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        this.running[done.Task] = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                TmdbDataService data = scope.ServiceProvider.GetRequiredService<TmdbDataService>();
                int purged = await data.PurgeAllAsync(this.stopping.Token);
                if (this.running.Count <= 1)
                {
                    await data.ClearPurgePendingAsync(CancellationToken.None);
                }

                done.SetResult(purged);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && this.stopping.IsCancellationRequested)
            {
                done.SetResult(null);
            }
            catch (Exception ex)
            {
                this.LogFailed(ex);
                done.SetResult(null);
            }
            finally
            {
                this.running.TryRemove(done.Task, out _);
            }
        });
        return done.Task;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Purging all stored TMDB data failed; it stays pending and is retried.")]
    private partial void LogFailed(Exception exception);
}

public sealed class TmdbPurgeHostedService(TmdbPurgeRunner runner) : IHostedService
{
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(10);

    public Task StartAsync(CancellationToken cancellationToken) => runner.ResumeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => runner.DrainAsync(DrainLimit);
}
