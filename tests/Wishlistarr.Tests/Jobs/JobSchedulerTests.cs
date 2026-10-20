using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Jobs;

public sealed class JobSchedulerTests
{
    [Fact]
    public async Task Job_runs_when_it_has_never_run_and_again_once_the_interval_has_elapsed()
    {
        await using JobHarness harness = await JobHarness.CreateAsync(TimeSpan.FromHours(1));
        harness.Probe.Block = false;
        JobScheduler scheduler = harness.Provider.GetRequiredService<JobScheduler>();

        await scheduler.RunDueAsync();
        await WaitForIdleAsync(harness);
        Assert.Equal(1, harness.Probe.Runs);

        harness.Time.Advance(TimeSpan.FromMinutes(59));
        await scheduler.RunDueAsync();
        await WaitForIdleAsync(harness);
        Assert.Equal(1, harness.Probe.Runs);

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await scheduler.RunDueAsync();
        await WaitForIdleAsync(harness);
        Assert.Equal(2, harness.Probe.Runs);
        Assert.All(await harness.GetRunsAsync(), run => Assert.Equal(JobRunTrigger.Schedule, run.Trigger));
    }

    [Fact]
    public async Task Configured_interval_overrides_the_default()
    {
        await using JobHarness harness = await JobHarness.CreateAsync(
            TimeSpan.FromHours(1),
            new Dictionary<string, string?> { [$"Jobs:Schedules:{ProbeJob.Name}:Interval"] = "00:05:00" });
        harness.Probe.Block = false;
        JobScheduler scheduler = harness.Provider.GetRequiredService<JobScheduler>();
        await scheduler.RunDueAsync();
        await WaitForIdleAsync(harness);

        harness.Time.Advance(TimeSpan.FromMinutes(5));
        await scheduler.RunDueAsync();
        await WaitForIdleAsync(harness);

        Assert.Equal(2, harness.Probe.Runs);
    }

    [Fact]
    public async Task Disabled_job_is_not_scheduled_but_can_still_be_triggered_manually()
    {
        await using JobHarness harness = await JobHarness.CreateAsync(
            TimeSpan.FromHours(1),
            new Dictionary<string, string?> { [$"Jobs:Schedules:{ProbeJob.Name}:Enabled"] = "false" });
        harness.Probe.Block = false;
        JobScheduler scheduler = harness.Provider.GetRequiredService<JobScheduler>();

        await scheduler.RunDueAsync();
        Assert.Equal(0, harness.Probe.Runs);

        JobStartResult result = await harness.Runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await result.Completion;
        Assert.Equal(1, harness.Probe.Runs);
    }

    [Fact]
    public async Task Job_without_an_interval_is_only_run_manually()
    {
        await using JobHarness harness = await JobHarness.CreateAsync();
        JobScheduler scheduler = harness.Provider.GetRequiredService<JobScheduler>();

        await scheduler.RunDueAsync();

        Assert.Equal(0, harness.Probe.Runs);
        Assert.Empty(await harness.GetRunsAsync());
    }

    [Fact]
    public async Task Due_job_that_is_still_running_is_not_started_again()
    {
        await using JobHarness harness = await JobHarness.CreateAsync(TimeSpan.FromMinutes(1));
        JobScheduler scheduler = harness.Provider.GetRequiredService<JobScheduler>();
        await scheduler.RunDueAsync();
        await harness.Probe.Started;

        harness.Time.Advance(TimeSpan.FromHours(1));
        await scheduler.RunDueAsync();

        Assert.Equal(1, harness.Probe.Runs);
        harness.Probe.Release();
        await WaitForIdleAsync(harness);
        Assert.Equal(1, harness.Probe.MaxActive);
    }

    [Fact]
    public void Non_positive_intervals_are_invalid()
    {
        JobOptions options = new();
        options.Schedules["x"] = new JobScheduleOptions { Interval = TimeSpan.Zero };

        Assert.False(options.IsValid());
    }

    private static async Task WaitForIdleAsync(JobHarness harness)
    {
        while (harness.Runner.IsRunning(ProbeJob.Name))
        {
            await Task.Yield();
        }

        JobRun? last = await harness.Runner.GetLastRunAsync(ProbeJob.Name);
        while (last is { Outcome: JobRunOutcome.Running })
        {
            await Task.Yield();
            last = await harness.Runner.GetLastRunAsync(ProbeJob.Name);
        }
    }
}
