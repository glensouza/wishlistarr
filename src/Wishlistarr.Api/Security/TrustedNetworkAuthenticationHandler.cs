using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

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
        if (!trustedNetworks.Contains(this.Context.Connection.RemoteIpAddress))
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

    public bool Contains(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        IPAddress normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return this.networks.Any(network => network.Contains(normalized));
    }
}
