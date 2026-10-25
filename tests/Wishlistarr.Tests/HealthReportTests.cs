using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.System;
using Wishlistarr.Tests.Jobs;

namespace Wishlistarr.Tests;

public sealed class HealthReportTests
{
    private static readonly string[] Names = ["plex", "tmdb", "seerr", "sonarr", "radarr", "webhooks", "jobs", "database"];

    [Fact]
    public async Task Two_calls_in_a_row_reach_the_upstream_once()
    {
        using Harness harness = new();

        await harness.Report.CheckAsync();
        IReadOnlyList<HealthCheckResult> second = await harness.Report.CheckAsync();

        Assert.All(harness.Checks, check => Assert.Equal(1, check.Calls));
        Assert.Equal(8, second.Count);
    }

    [Fact]
    public async Task Concurrent_calls_share_one_run()
    {
        using Harness harness = new(gated: true);

        Task<IReadOnlyList<HealthCheckResult>>[] calls = [.. Enumerable.Range(0, 6).Select(_ => harness.Report.CheckAsync())];
        await Task.WhenAll(harness.Checks.Select(check => check.Entered));
        harness.Release();
        IReadOnlyList<HealthCheckResult>[] results = await Task.WhenAll(calls);

        Assert.All(harness.Checks, check => Assert.Equal(1, check.Calls));
        Assert.All(results, result => Assert.Same(results[0], result));
    }

    [Fact]
    public async Task The_report_runs_again_after_the_cache_duration()
    {
        using Harness harness = new();

        await harness.Report.CheckAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(29));
        await harness.Report.CheckAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await harness.Report.CheckAsync();

        Assert.All(harness.Checks, check => Assert.Equal(2, check.Calls));
    }

    [Fact]
    public async Task A_failure_is_only_kept_for_the_cache_duration()
    {
        using Harness harness = new();
        harness.Checks[0].Throw = new InvalidOperationException("down");

        IReadOnlyList<HealthCheckResult> failed = await harness.Report.CheckAsync();
        harness.Checks[0].Throw = null;
        IReadOnlyList<HealthCheckResult> stillFailed = await harness.Report.CheckAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(31));
        IReadOnlyList<HealthCheckResult> recovered = await harness.Report.CheckAsync();

        Assert.Equal(HealthCheckStatus.Error, failed[0].Status);
        Assert.Equal(HealthCheckStatus.Error, stillFailed[0].Status);
        Assert.Equal(HealthCheckStatus.Ok, recovered[0].Status);
    }

    [Fact]
    public async Task A_check_that_does_not_answer_is_an_error_with_a_fixed_message()
    {
        using Harness harness = new(timeout: TimeSpan.FromMilliseconds(100));
        harness.Checks[1].Hang = true;

        IReadOnlyList<HealthCheckResult> results = await harness.Report.CheckAsync();

        Assert.Equal(HealthCheckStatus.Error, results[1].Status);
        Assert.Equal("tmdb name did not answer in time.", results[1].Message);
        Assert.Equal(HealthCheckStatus.Ok, results[0].Status);
    }

    [Fact]
    public async Task A_check_that_throws_is_an_error_without_the_exception_detail()
    {
        using Harness harness = new();
        harness.Checks[2].Throw = new InvalidOperationException("password=throw-secret-qq at /data/wishlistarr.db");

        IReadOnlyList<HealthCheckResult> results = await harness.Report.CheckAsync();

        Assert.Equal(HealthCheckStatus.Error, results[2].Status);
        Assert.Equal("seerr name could not be checked.", results[2].Message);
        Assert.DoesNotContain("throw-secret-qq", results[2].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("wishlistarr.db", results[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_that_gives_up_gets_a_cancellation_and_nothing_partial_is_cached()
    {
        using Harness harness = new(gated: true);
        using CancellationTokenSource cancel = new();

        Task<IReadOnlyList<HealthCheckResult>> abandoned = harness.Report.CheckAsync(cancel.Token);
        await Task.WhenAll(harness.Checks.Select(check => check.Entered));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        harness.Release();
        IReadOnlyList<HealthCheckResult> results = await harness.Report.CheckAsync();

        Assert.All(results, result => Assert.Equal(HealthCheckStatus.Ok, result.Status));
        Assert.All(harness.Checks, check => Assert.Equal(1, check.Calls));
    }

    private sealed class FakeCheck(string name) : IHealthCheck
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public string Name => name;

        public string DisplayName => name + " name";

        public string Fix => "fix";

        public int Calls => this.calls;

        public Task Entered => this.entered.Task;

        public Exception? Throw { get; set; }

        public bool Hang { get; set; }

        public Task? Gate { get; init; }

        public async Task<HealthCheckResult> CheckAsync(IServiceProvider services, CancellationToken ct)
        {
            Interlocked.Increment(ref this.calls);
            this.entered.TrySetResult();
            if (this.Gate is not null)
            {
                await this.Gate;
            }

            if (this.Hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (this.Throw is not null)
            {
                throw this.Throw;
            }

            return new HealthCheckResult(name, HealthCheckStatus.Ok, "fine", "fix");
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Harness(bool gated = false, TimeSpan? timeout = null)
        {
            this.Checks = [.. Names.Select(name => new FakeCheck(name) { Gate = gated ? this.gate.Task : null })];
            this.Time = new ManualTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            this.Report = new HealthReport(
                this.Checks,
                this.provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new HealthOptions { CacheDuration = TimeSpan.FromSeconds(30), CheckTimeout = timeout ?? TimeSpan.FromSeconds(8) }),
                this.Time);
        }

        public FakeCheck[] Checks { get; }

        public ManualTime Time { get; }

        public HealthReport Report { get; }

        public void Release() => this.gate.TrySetResult();

        public void Dispose() => this.provider.Dispose();
    }
}
