using System.Reflection;

namespace Wishlistarr.Api.System;

public sealed class SystemStatus(TimeProvider time)
{
    private readonly DateTimeOffset startedAt = time.GetUtcNow();

    public StatusResponse Get() => new(GetVersion(), (long)(time.GetUtcNow() - this.startedAt).TotalSeconds);

    private static string GetVersion()
    {
        string? version = typeof(SystemStatus).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return version?.Split('+')[0] ?? "0.0.0";
    }
}

public sealed record StatusResponse(string Version, long UptimeSeconds);
