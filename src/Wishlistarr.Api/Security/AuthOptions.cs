using System.Net;

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

    public List<string> KnownProxies { get; set; } = [];

    public bool IsValid() =>
        this.MaxFailedLogins > 0
        && this.LockoutDuration > TimeSpan.Zero
        && this.LoginAttemptsPerMinute > 0
        && this.SessionLifetime > TimeSpan.Zero
        && this.TrustedNetworks.All(network => IPNetwork.TryParse(network, out _))
        && this.KnownProxies.All(proxy => IPAddress.TryParse(proxy, out _));
}
