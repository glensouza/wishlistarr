using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.System;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Jobs;

public sealed class JobRunner(
    IEnumerable<JobDefinition> registered,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<JobRunner> logger)
{
    private readonly IReadOnlyDictionary<string, JobDefinition> definitions =
        registered.ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, Task> running = new();
    private readonly CancellationTokenSource stopping = new();

    public IReadOnlyCollection<JobDefinition> Definitions => [.. this.definitions.Values];

    public JobDefinition? Find(string name) => this.definitions.GetValueOrDefault(name);

    public bool IsRunning(string name) =>
        this.gates.TryGetValue(name, out SemaphoreSlim? gate) && gate.CurrentCount == 0;

    public async Task<JobStartResult> StartAsync(string name, JobRunTrigger trigger, CancellationToken ct = default)
    {
        if (this.Find(name) is not { } definition)
        {
            return new JobStartResult(JobStartStatus.NotFound, null, Task.CompletedTask);
        }

        if (this.stopping.IsCancellationRequested)
        {
            return new JobStartResult(JobStartStatus.Stopping, null, Task.CompletedTask);
        }

        SemaphoreSlim gate = this.gates.GetOrAdd(definition.Name, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, CancellationToken.None))
        {
            return new JobStartResult(JobStartStatus.AlreadyRunning, null, Task.CompletedTask);
        }

        try
        {
            int runId = await this.RecordStartAsync(definition, trigger, ct);
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            this.running[runId] = completion.Task;
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await this.ExecuteAsync(definition, runId);
                    }
                    finally
                    {
                        gate.Release();
                        this.running.TryRemove(runId, out _);
                        completion.SetResult();
                    }
                },
                CancellationToken.None);
            return new JobStartResult(JobStartStatus.Started, runId, completion.Task);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await this.stopping.CancelAsync();
        await Task.WhenAll(this.running.Values).WaitAsync(ct);
    }

    public async Task RecoverInterruptedAsync(CancellationToken ct = default)
    {
        using IServiceScope scope = scopes.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        DateTimeOffset now = time.GetUtcNow();
        List<JobRun> interrupted = await db.JobRuns.Where(run => run.Outcome == JobRunOutcome.Running).ToListAsync(ct);
        foreach (JobRun run in interrupted)
        {
            run.Outcome = JobRunOutcome.Failed;
            run.FinishedAt = now;
            run.Error = "The application stopped before the run finished.";
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<JobRun?> GetLastRunAsync(string name, CancellationToken ct = default)
    {
        using IServiceScope scope = scopes.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        return await db.JobRuns.AsNoTracking()
            .Where(run => run.JobName == name)
            .OrderByDescending(run => run.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<JobRun>> ListRunsAsync(string name, int limit, CancellationToken ct = default)
    {
        using IServiceScope scope = scopes.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        return await db.JobRuns.AsNoTracking()
            .Where(run => run.JobName == name)
            .OrderByDescending(run => run.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    private async Task<int> RecordStartAsync(JobDefinition definition, JobRunTrigger trigger, CancellationToken ct)
    {
        using IServiceScope scope = scopes.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        JobRun run = new()
        {
            JobName = definition.Name,
            Trigger = trigger,
            StartedAt = time.GetUtcNow(),
            Outcome = JobRunOutcome.Running,
        };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    private async Task ExecuteAsync(JobDefinition definition, int runId)
    {
        JobRunOutcome outcome;
        string? error = null;
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            IBackgroundJob job = (IBackgroundJob)scope.ServiceProvider.GetRequiredService(definition.JobType);
            await job.RunAsync(this.stopping.Token);
            outcome = JobRunOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (this.stopping.IsCancellationRequested)
        {
            outcome = JobRunOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Job {Job} failed.", definition.Name);
            outcome = JobRunOutcome.Failed;
            error = exception.Message.Length > JobRun.MaxErrorLength
                ? exception.Message[..JobRun.MaxErrorLength]
                : exception.Message;
        }

        AppMetrics.RecordJob(definition.Name, outcome.ToString().ToLowerInvariant());

        try
        {
            using IServiceScope scope = scopes.CreateScope();
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            JobRun run = await db.JobRuns.SingleAsync(item => item.Id == runId);
            run.Outcome = outcome;
            run.Error = error;
            run.FinishedAt = time.GetUtcNow();
            await db.SaveChangesAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not record the result of job {Job}.", definition.Name);
        }
    }
}

public enum JobStartStatus
{
    Started,
    NotFound,
    AlreadyRunning,
    Stopping,
}

public sealed record JobStartResult(JobStartStatus Status, int? RunId, Task Completion);
