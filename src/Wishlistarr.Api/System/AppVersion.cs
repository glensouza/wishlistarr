using System.Reflection;

namespace Wishlistarr.Api.System;

public static class AppVersion
{
    public static string Current { get; } = Normalize(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string Normalize(string? informational)
    {
        string? version = informational?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version) ? "0.0.0" : version;
    }
}
