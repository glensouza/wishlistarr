using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Jobs;

namespace Wishlistarr.Api.System;

public sealed class SystemStatus(TimeProvider time, JobRunner jobs, IOptions<JobOptions> jobOptions)
{
    private readonly DateTimeOffset startedAt = new(Process.GetCurrentProcess().StartTime.ToUniversalTime());

    public async Task<StatusResponse> GetAsync(CancellationToken ct = default) => new(
        GetVersion(),
        (long)(time.GetUtcNow() - this.startedAt).TotalSeconds,
        await jobs.ListJobsAsync(jobOptions.Value, ct));

    private static string GetVersion()
    {
        string? version = typeof(SystemStatus).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return version?.Split('+')[0] ?? "0.0.0";
    }
}

public sealed record StatusResponse(string Version, long UptimeSeconds, IReadOnlyList<JobSummary> Jobs);
