using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Kids;

namespace Wishlistarr.Api.Security;

public sealed class TrustedNetworkAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TrustedNetworks trustedNetworks)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (this.Context.Request.Cookies.ContainsKey(KidDevices.CookieName) || !trustedNetworks.IsTrusted(this.Context))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        AuthenticationTicket ticket = new(
            Principals.CreateParent("trusted-network", "Trusted network", this.Scheme.Name),
            this.Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class TrustedNetworks(IOptions<AuthOptions> options)
{
    private readonly IPNetwork[] networks = options.Value.TrustedNetworkBypass
        ? [.. options.Value.TrustedNetworks.Select(network => IPNetwork.Parse(network))]
        : [];

    private readonly HashSet<string> hosts = new(options.Value.TrustedHosts, StringComparer.OrdinalIgnoreCase);

    public bool IsTrusted(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;

        // Checking the Host header stops a DNS-rebinding page from borrowing a trusted browser's address.
        if (address is null || !this.hosts.Contains(context.Request.Host.Host))
        {
            return false;
        }

        IPAddress normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return this.networks.Any(network => network.Contains(normalized));
    }
}
