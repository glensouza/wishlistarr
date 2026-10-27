using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.System;
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

    [Theory]
    [InlineData(false, "succeeded")]
    [InlineData(true, "failed")]
    public async Task A_finished_run_adds_one_to_the_job_counter_with_its_outcome(bool fail, string outcome)
    {
        List<(string Job, string Outcome)> seen = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AppMetrics.Name && instrument.Name == "wishlistarr.jobs")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string job = string.Empty;
            string finished = string.Empty;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "job")
                {
                    job = (string)tag.Value!;
                }
                else if (tag.Key == "outcome")
                {
                    finished = (string)tag.Value!;
                }
            }

            if (value == 1)
            {
                lock (seen)
                {
                    seen.Add((job, finished));
                }
            }
        });
        listener.Start();
        await using JobHarness harness = await JobHarness.CreateAsync();
        harness.Probe.Block = false;
        harness.Probe.Failure = fail ? new InvalidOperationException("boom") : null;

        JobStartResult result = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await result.Completion;

        lock (seen)
        {
            Assert.Contains((ProbeJob.Name, outcome), seen);
        }
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
    public async Task A_start_that_asks_for_a_rerun_runs_the_job_once_more_after_the_current_run()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();
        JobStartResult first = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await harness.Probe.Started;

        JobStartResult kick = await harness.Runner.StartAsync(
            ProbeJob.Name, JobRunTrigger.Request, rerunIfRunning: true);
        Assert.Equal(JobStartStatus.AlreadyRunning, kick.Status);
        Assert.Equal(1, harness.Probe.Runs);
        harness.Probe.Block = false;
        harness.Probe.Release();
        await first.Completion;
        await WaitForFinishedRunsAsync(harness, 2);

        Assert.Equal(2, harness.Probe.Runs);
        Assert.Equal(1, harness.Probe.MaxActive);
        List<JobRun> runs = await harness.GetRunsAsync();
        Assert.Equal(JobRunTrigger.Manual, runs[0].Trigger);
        Assert.Equal(JobRunTrigger.Request, runs[1].Trigger);
        Assert.All(runs, run => Assert.Equal(JobRunOutcome.Succeeded, run.Outcome));
    }

    [Fact]
    public async Task A_burst_of_rerun_requests_adds_exactly_one_run()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();
        JobStartResult first = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await harness.Probe.Started;

        JobStartResult[] kicks = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Request, rerunIfRunning: true))));
        harness.Probe.Block = false;
        harness.Probe.Release();
        await first.Completion;
        await WaitForFinishedRunsAsync(harness, 2);
        await harness.Runner.StopAsync();

        Assert.All(kicks, kick => Assert.Equal(JobStartStatus.AlreadyRunning, kick.Status));
        Assert.Equal(2, harness.Probe.Runs);
        Assert.Equal(2, (await harness.GetRunsAsync()).Count);
    }

    [Fact]
    public async Task A_start_without_the_rerun_request_leaves_no_extra_run()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();
        JobStartResult first = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await harness.Probe.Started;

        JobStartResult kick = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Request);
        harness.Probe.Release();
        await first.Completion;
        await harness.Runner.StopAsync();

        Assert.Equal(JobStartStatus.AlreadyRunning, kick.Status);
        Assert.Equal(1, harness.Probe.Runs);
        Assert.Single(await harness.GetRunsAsync());
    }

    [Fact]
    public async Task A_rerun_request_after_the_job_finished_its_work_but_before_the_run_closed_is_not_lost()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();
        JobStartResult first = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await harness.Probe.Started;
        using ManualResetEventSlim pause = new(false);
        harness.Time.Pause = pause;
        harness.Probe.Release();
        await Task.Run(() => harness.Time.Reached.Wait(TimeSpan.FromSeconds(10)));

        JobStartResult kick = await harness.Runner.StartAsync(
            ProbeJob.Name, JobRunTrigger.Request, rerunIfRunning: true);
        harness.Probe.Block = false;
        pause.Set();
        await first.Completion;
        await WaitForFinishedRunsAsync(harness, 2);

        Assert.Equal(JobStartStatus.AlreadyRunning, kick.Status);
        Assert.Equal(2, harness.Probe.Runs);
    }

    [Fact]
    public async Task A_run_that_fails_still_starts_the_requested_rerun()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();
        harness.Probe.Failure = new InvalidOperationException("boom");
        JobStartResult first = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await harness.Probe.Started;
        await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Webhook, rerunIfRunning: true);
        harness.Probe.Block = false;
        harness.Probe.Release();
        await first.Completion;
        await WaitForFinishedRunsAsync(harness, 2);

        List<JobRun> runs = await harness.GetRunsAsync();
        Assert.Equal(JobRunOutcome.Failed, runs[0].Outcome);
        Assert.Equal(JobRunTrigger.Webhook, runs[1].Trigger);
        Assert.Equal(2, harness.Probe.Runs);
    }

    [Fact]
    public async Task No_rerun_starts_once_the_runner_is_stopping()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();
        await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await harness.Probe.Started;
        await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Request, rerunIfRunning: true);

        await harness.Runner.StopAsync();

        Assert.Equal(1, harness.Probe.Runs);
        JobRun run = Assert.Single(await harness.GetRunsAsync());
        Assert.Equal(JobRunOutcome.Cancelled, run.Outcome);
        Assert.False(harness.Runner.IsRunning(ProbeJob.Name));
    }

    private static async Task WaitForFinishedRunsAsync(JobHarness harness, int count)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            List<JobRun> runs = await harness.GetRunsAsync();
            if (runs.Count >= count && runs.All(run => run.Outcome != JobRunOutcome.Running))
            {
                return;
            }

            await Task.Delay(10, timeout.Token);
        }
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
