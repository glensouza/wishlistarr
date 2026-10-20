using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Jobs;

public sealed class JobRunnerTests
{
    [Fact]
    public async Task Successful_run_records_start_end_and_outcome()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();
        harness.Probe.Block = false;
        DateTimeOffset started = harness.Time.GetUtcNow();

        JobStartResult result = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await result.Completion;

        Assert.Equal(JobStartStatus.Started, result.Status);
        JobRun run = Assert.Single(await harness.GetRunsAsync());
        Assert.Equal(ProbeJob.Name, run.JobName);
        Assert.Equal(JobRunTrigger.Manual, run.Trigger);
        Assert.Equal(JobRunOutcome.Succeeded, run.Outcome);
        Assert.Equal(started, run.StartedAt);
        Assert.NotNull(run.FinishedAt);
        Assert.True(run.FinishedAt >= run.StartedAt);
        Assert.Null(run.Error);
    }

    [Fact]
    public async Task Failed_run_records_the_error()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();
        harness.Probe.Block = false;
        harness.Probe.Failure = new InvalidOperationException("boom");

        JobStartResult result = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await result.Completion;

        JobRun run = Assert.Single(await harness.GetRunsAsync());
        Assert.Equal(JobRunOutcome.Failed, run.Outcome);
        Assert.Equal("boom", run.Error);
        Assert.NotNull(run.FinishedAt);
        Assert.False(harness.Runner.IsRunning(ProbeJob.Name));
    }

    [Fact]
    public async Task Unknown_job_is_not_found()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();

        JobStartResult result = await harness.Runner.StartAsync("missing", JobRunTrigger.Manual);

        Assert.Equal(JobStartStatus.NotFound, result.Status);
        Assert.Empty(await harness.GetRunsAsync());
    }

    [Fact]
    public async Task A_job_that_is_running_is_not_started_again()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();

        JobStartResult first = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await harness.Probe.Started;
        JobStartResult second = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Schedule);

        Assert.Equal(JobStartStatus.Started, first.Status);
        Assert.Equal(JobStartStatus.AlreadyRunning, second.Status);
        Assert.True(harness.Runner.IsRunning(ProbeJob.Name));

        harness.Probe.Release();
        await first.Completion;

        Assert.False(harness.Runner.IsRunning(ProbeJob.Name));
        JobStartResult third = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        Assert.Equal(JobStartStatus.Started, third.Status);
        await third.Completion;
        Assert.Equal(2, (await harness.GetRunsAsync()).Count);
        Assert.Equal(1, harness.Probe.MaxActive);
    }

    [Fact]
    public async Task Simultaneous_starts_run_the_job_once()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();

        JobStartResult[] results = await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(_ => Task.Run(() => harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual))));
        await harness.Probe.Started;
        harness.Probe.Release();
        await Task.WhenAll(results.Select(result => result.Completion));

        Assert.Single(results, result => result.Status == JobStartStatus.Started);
        Assert.Equal(24, results.Count(result => result.Status == JobStartStatus.AlreadyRunning));
        Assert.Equal(1, harness.Probe.MaxActive);
        Assert.Single(await harness.GetRunsAsync());
    }

    [Fact]
    public async Task Stopping_cancels_running_jobs_and_records_them_as_cancelled()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();
        JobStartResult result = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await harness.Probe.Started;

        await harness.Runner.StopAsync();

        Assert.True(result.Completion.IsCompletedSuccessfully);
        JobRun run = Assert.Single(await harness.GetRunsAsync());
        Assert.Equal(JobRunOutcome.Cancelled, run.Outcome);
        Assert.NotNull(run.FinishedAt);
        Assert.False(harness.Runner.IsRunning(ProbeJob.Name));
        JobStartResult late = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        Assert.Equal(JobStartStatus.Stopping, late.Status);
    }

    [Fact]
    public async Task Stopping_the_scheduler_cancels_running_jobs()
    {
        await using JobHarness harness = await JobHarness.CreateAsync(TimeSpan.FromHours(1));
        JobScheduler scheduler = harness.Provider.GetRequiredService<JobScheduler>();
        await scheduler.StartAsync(CancellationToken.None);
        await harness.Probe.Started;

        await scheduler.StopAsync(CancellationToken.None);

        JobRun run = Assert.Single(await harness.GetRunsAsync());
        Assert.Equal(JobRunOutcome.Cancelled, run.Outcome);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task Runs_left_open_by_a_previous_process_are_closed_as_failed()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();
        using (IServiceScope scope = harness.Provider.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            db.JobRuns.Add(new JobRun
            {
                JobName = ProbeJob.Name,
                StartedAt = harness.Time.GetUtcNow(),
                Outcome = JobRunOutcome.Running,
            });
            await db.SaveChangesAsync();
        }

        await harness.Runner.RecoverInterruptedAsync();

        JobRun run = Assert.Single(await harness.GetRunsAsync());
        Assert.Equal(JobRunOutcome.Failed, run.Outcome);
        Assert.NotNull(run.FinishedAt);
        Assert.NotNull(run.Error);
    }

    [Fact]
    public async Task Cleanup_removes_only_finished_runs_older_than_the_retention()
    {
        await using JobHarness harness = await JobHarness.CreateAsync(settings: new Dictionary<string, string?>
        {
            ["Jobs:HistoryRetention"] = "10.00:00:00",
        });
        DateTimeOffset now = harness.Time.GetUtcNow();
        using (IServiceScope scope = harness.Provider.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            db.JobRuns.AddRange(
                new JobRun { JobName = "a", StartedAt = now.AddDays(-30), Outcome = JobRunOutcome.Succeeded },
                new JobRun { JobName = "a", StartedAt = now.AddDays(-30), Outcome = JobRunOutcome.Running },
                new JobRun { JobName = "a", StartedAt = now.AddDays(-1), Outcome = JobRunOutcome.Failed });
            await db.SaveChangesAsync();
        }

        using (IServiceScope scope = harness.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobHistoryCleanup>().RunAsync(CancellationToken.None);
        }

        List<JobRun> remaining = await harness.GetRunsAsync();
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, run => run.Outcome == JobRunOutcome.Succeeded);
    }
}
