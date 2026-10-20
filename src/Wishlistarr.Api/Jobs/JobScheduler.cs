using Microsoft.Extensions.Options;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Jobs;

public sealed class JobScheduler(
    JobRunner runner,
    IOptions<JobOptions> options,
    TimeProvider time,
    ILogger<JobScheduler> logger) : BackgroundService
{
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await runner.StopAsync(cancellationToken);
    }

    public async Task RunDueAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = time.GetUtcNow();
        foreach (JobDefinition definition in runner.Definitions)
        {
            if (options.Value.Resolve(definition).Interval is not { } interval)
            {
                continue;
            }

            JobRun? last = await runner.GetLastRunAsync(definition.Name, ct);
            if (last is null || now - last.StartedAt >= interval)
            {
                await runner.StartAsync(definition.Name, JobRunTrigger.Schedule, ct);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await runner.RecoverInterruptedAsync(stoppingToken);
        using PeriodicTimer timer = new(options.Value.PollInterval, time);
        do
        {
            try
            {
                await this.RunDueAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Scheduling jobs failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
