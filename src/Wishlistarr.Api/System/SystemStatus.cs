using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Library;

namespace Wishlistarr.Api.System;

public sealed class SystemStatus(TimeProvider time, JobRunner jobs, IOptions<JobOptions> jobOptions, IServiceScopeFactory scopes)
{
    private readonly DateTimeOffset startedAt = new(Process.GetCurrentProcess().StartTime.ToUniversalTime());

    public async Task<StatusResponse> GetAsync(CancellationToken ct = default)
    {
        using IServiceScope scope = scopes.CreateScope();
        return new StatusResponse(
            GetVersion(),
            (long)(time.GetUtcNow() - this.startedAt).TotalSeconds,
            await jobs.ListJobsAsync(jobOptions.Value, ct),
            await scope.ServiceProvider.GetRequiredService<LibraryIndex>().GetStatusAsync(ct));
    }

    private static string GetVersion()
    {
        string? version = typeof(SystemStatus).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return version?.Split('+')[0] ?? "0.0.0";
    }
}

public sealed record StatusResponse(string Version, long UptimeSeconds, IReadOnlyList<JobSummary> Jobs, LibraryStatus Library);
