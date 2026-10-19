using System.Net;
using System.Net.Sockets;

namespace Wishlistarr.Api.Security;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public int MaxFailedLogins { get; set; } = 5;

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    public int LoginAttemptsPerMinute { get; set; } = 10;

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);

    public bool TrustedNetworkBypass { get; set; }

    public List<string> TrustedNetworks { get; set; } = [];

    public List<string> TrustedHosts { get; set; } = [];

    public List<string> KnownProxies { get; set; } = [];

    public bool IsValid() =>
        this.MaxFailedLogins > 0
        && this.LockoutDuration > TimeSpan.Zero
        && this.LoginAttemptsPerMinute > 0
        && this.SessionLifetime > TimeSpan.Zero
        && this.TrustedNetworks.All(IsNetwork)
        && this.KnownProxies.All(IsAddress)
        && this.TrustedHosts.All(host => !string.IsNullOrWhiteSpace(host) && !host.Contains('*') && !host.Contains(':'));

    public bool HasTrustedHostsForBypass() => !this.TrustedNetworkBypass || this.TrustedHosts.Count > 0;

    public static bool IsAddress(string value)
    {
        if (!IPAddress.TryParse(value, out IPAddress? address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return value.Contains(':');
        }

        string[] parts = value.Split('.');
        return parts.Length == 4 && parts.All(part => part.Length is > 0 and <= 3 && part.All(char.IsAsciiDigit));
    }

    public static bool IsNetwork(string value)
    {
        string[] parts = value.Split('/');
        return parts.Length == 2
            && IsAddress(parts[0])
            && parts[1].Length > 0
            && parts[1].All(char.IsAsciiDigit)
            && IPNetwork.TryParse(value, out _);
    }
}
