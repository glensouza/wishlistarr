using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wishlistarr.Api.Security;

namespace Wishlistarr.Tests;

public sealed class TrustedNetworkTests
{
    [Fact]
    public async Task Bypass_is_off_by_default()
    {
        using AuthApiFactory factory = new();
        await factory.EnsureParentAsync();

        HttpResponseMessage response = await SendAsync(factory, "192.168.1.5");

        Assert.False(factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value.TrustedNetworkBypass);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Configured_networks_are_ignored_until_the_bypass_is_enabled()
    {
        using AuthApiFactory factory = Create(enabled: false);
        await factory.EnsureParentAsync();

        HttpResponseMessage response = await SendAsync(factory, "192.168.1.5");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Enabled_bypass_admits_only_trusted_addresses_as_parent()
    {
        using AuthApiFactory factory = Create(enabled: true);
        await factory.EnsureParentAsync();

        HttpResponseMessage trusted = await SendAsync(factory, "192.168.1.5");
        HttpResponseMessage mapped = await SendAsync(factory, "::ffff:192.168.1.6");
        HttpResponseMessage untrusted = await SendAsync(factory, "10.0.0.5");
        HttpResponseMessage noAddress = await SendAsync(factory, null);
        HttpRequestMessage statusRequest = new(HttpMethod.Get, "/api/v1/auth/status");
        statusRequest.Headers.Add(AuthApiFactory.RemoteIpHeader, "192.168.1.5");
        JsonElement status = await (await factory.CreateClient().SendAsync(statusRequest)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, trusted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mapped.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, untrusted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noAddress.StatusCode);
        Assert.Equal(Principals.Parent, status.GetProperty("principalType").GetString());
    }

    [Fact]
    public async Task Bypass_requires_a_trusted_host_name()
    {
        using AuthApiFactory factory = Create(enabled: true);
        await factory.EnsureParentAsync();

        HttpResponseMessage trustedHost = await SendAsync(factory, "192.168.1.5", host: "LOCALHOST:8080");
        HttpResponseMessage rebound = await SendAsync(factory, "192.168.1.5", host: "attacker.example");
        HttpResponseMessage address = await SendAsync(factory, "192.168.1.5", host: "192.168.1.10");

        Assert.Equal(HttpStatusCode.OK, trustedHost.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rebound.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, address.StatusCode);
    }

    [Fact]
    public void Bypass_without_trusted_hosts_is_rejected()
    {
        Assert.True(new AuthOptions { TrustedHosts = [] }.HasTrustedHostsForBypass());
        Assert.False(new AuthOptions { TrustedNetworkBypass = true, TrustedNetworks = ["192.168.1.0/24"] }.HasTrustedHostsForBypass());
        Assert.True(new AuthOptions { TrustedNetworkBypass = true, TrustedHosts = ["wishlistarr.lan"] }.HasTrustedHostsForBypass());
        Assert.False(new AuthOptions { TrustedHosts = ["*"] }.IsValid());
        Assert.False(new AuthOptions { TrustedHosts = ["*.lan"] }.IsValid());
        Assert.False(new AuthOptions { TrustedHosts = ["wishlistarr.lan:8080"] }.IsValid());
    }

    [Fact]
    public async Task Forwarded_for_is_ignored_without_configured_known_proxies()
    {
        using AuthApiFactory factory = Create(enabled: true);
        await factory.EnsureParentAsync();

        HttpResponseMessage response = await SendAsync(factory, "10.0.0.1", forwardedFor: "192.168.1.5");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Forwarded_for_is_honored_only_from_known_proxies()
    {
        using AuthApiFactory factory = Create(enabled: true, knownProxy: "10.0.0.1");
        await factory.EnsureParentAsync();

        HttpResponseMessage fromKnownProxy = await SendAsync(factory, "10.0.0.1", forwardedFor: "192.168.1.5");
        HttpResponseMessage fromUnknownProxy = await SendAsync(factory, "10.0.0.2", forwardedFor: "192.168.1.5");

        Assert.Equal(HttpStatusCode.OK, fromKnownProxy.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, fromUnknownProxy.StatusCode);
    }

    [Fact]
    public async Task Known_proxy_inside_the_trusted_network_does_not_trust_outside_clients()
    {
        using AuthApiFactory factory = Create(enabled: true, knownProxy: "192.168.1.1");
        await factory.EnsureParentAsync();

        HttpResponseMessage response = await SendAsync(factory, "192.168.1.1", forwardedFor: "203.0.113.9");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Bypass_writes_still_require_an_antiforgery_token()
    {
        using AuthApiFactory factory = Create(enabled: true);
        await factory.EnsureParentAsync();
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthApiFactory.RemoteIpHeader, "192.168.1.5");

        HttpResponseMessage rejected = await client.PutAsJsonAsync("/api/v1/settings/lan.write", new { value = "x" });
        await AuthApiFactory.AddAntiforgeryTokenAsync(client);
        HttpResponseMessage accepted = await client.PutAsJsonAsync("/api/v1/settings/lan.write", new { value = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [Fact]
    public void Invalid_network_configuration_is_rejected()
    {
        Assert.True(new AuthOptions { TrustedNetworks = ["192.168.1.0/24", "fd00::/8"], KnownProxies = ["10.0.0.1"] }.IsValid());
        Assert.False(new AuthOptions { TrustedNetworks = ["not-a-network"] }.IsValid());
        Assert.False(new AuthOptions { TrustedNetworks = ["192.168.1.0/99"] }.IsValid());
        Assert.False(new AuthOptions { KnownProxies = ["proxy.local"] }.IsValid());
        Assert.False(new AuthOptions { KnownProxies = ["1"] }.IsValid());
        Assert.False(new AuthOptions { KnownProxies = ["10.1"] }.IsValid());
        Assert.False(new AuthOptions { KnownProxies = ["0x0a.0.0.1"] }.IsValid());
        Assert.False(new AuthOptions { TrustedNetworks = ["10.1/16"] }.IsValid());
        Assert.False(new AuthOptions { TrustedNetworks = ["192.168.1.0"] }.IsValid());
        Assert.False(new AuthOptions { TrustedNetworks = ["192.168.1.0/+24"] }.IsValid());
        Assert.True(new AuthOptions { KnownProxies = ["::1", "fd00::2"] }.IsValid());
    }

    private static AuthApiFactory Create(bool enabled, string? knownProxy = null)
    {
        Dictionary<string, string?> settings = new()
        {
            ["Auth:TrustedNetworkBypass"] = enabled ? "true" : "false",
            ["Auth:TrustedNetworks:0"] = "192.168.1.0/24",
            ["Auth:TrustedHosts:0"] = "localhost",
        };
        if (knownProxy is not null)
        {
            settings["Auth:KnownProxies:0"] = knownProxy;
        }

        return new AuthApiFactory { Settings = settings };
    }

    private static async Task<HttpResponseMessage> SendAsync(
        AuthApiFactory factory,
        string? remoteIp,
        string? forwardedFor = null,
        string? host = null)
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system/status");
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (remoteIp is not null)
        {
            request.Headers.Add(AuthApiFactory.RemoteIpHeader, remoteIp);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await factory.CreateClient().SendAsync(request);
    }
}
