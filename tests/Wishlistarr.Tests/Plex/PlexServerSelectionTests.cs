using System.Text.Json;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Plex;

namespace Wishlistarr.Tests.Plex;

public sealed class PlexServerSelectionTests
{
    [Fact]
    public void Connections_are_ordered_local_https_first_and_plain_remote_http_last()
    {
        PlexResource server = Resource(
            false,
            Connection("http", "http://203.0.113.10:32400", local: false),
            Connection("https", "https://relay.plex.direct:8443", local: false, relay: true),
            Connection("http", "http://192.168.1.20:32400", local: true),
            Connection("https", "https://203-0-113-10.abc.plex.direct:32400", local: false),
            Connection("https", "https://fd00--20.abc.plex.direct:32400", local: true, ipv6: true),
            Connection("https", "https://192-168-1-20.abc.plex.direct:32400", local: true));

        IReadOnlyList<MediaServerConnection> ordered = PlexConnections.Order(server);

        Assert.Equal(
            [
                "https://192-168-1-20.abc.plex.direct:32400/",
                "https://fd00--20.abc.plex.direct:32400/",
                "https://203-0-113-10.abc.plex.direct:32400/",
                "http://192.168.1.20:32400/",
                "https://relay.plex.direct:8443/",
                "http://203.0.113.10:32400/",
            ],
            ordered.Select(connection => connection.Uri.ToString()));
    }

    [Fact]
    public void Plain_http_is_dropped_when_the_server_requires_https()
    {
        PlexResource server = Resource(
            true,
            Connection("http", "http://192.168.1.20:32400", local: true),
            Connection("https", "https://203-0-113-10.abc.plex.direct:32400", local: false));

        MediaServerConnection connection = Assert.Single(PlexConnections.Order(server));

        Assert.True(connection.Secure);
    }

    [Theory]
    [InlineData("https://192-168-1-20.abc.plex.direct:32400", true)]
    [InlineData("http://192.168.1.20:32400/", true)]
    [InlineData("http://admin:secret@192.168.1.20:32400", false)]
    [InlineData("http://192.168.1.20:32400/other", false)]
    [InlineData("http://192.168.1.20:32400/?x=1", false)]
    [InlineData("http://192.168.1.20:32400/#x", false)]
    [InlineData("ftp://192.168.1.20/", false)]
    [InlineData("file:///etc/passwd", false)]
    public void Only_plain_http_or_https_base_addresses_are_allowed(string uri, bool allowed) =>
        Assert.Equal(allowed, PlexConnections.IsAllowed(new Uri(uri)));

    [Fact]
    public async Task Server_list_contains_only_servers_and_no_tokens()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAsync();

        IReadOnlyList<DiscoveredMediaServer> servers = await harness.Account.GetServersAsync();

        Assert.Equal([FakePlexServer.MachineIdentifier, FakePlexServer.SharedMachineIdentifier], servers.Select(server => server.Id));
        DiscoveredMediaServer home = servers[0];
        Assert.Equal("Living Room Server", home.Name);
        Assert.True(home.Owned);
        Assert.False(servers[1].Owned);
        Assert.Equal(6, home.Connections.Count);
        Assert.DoesNotContain(home.Connections, connection => connection.Uri.UserInfo.Length > 0 || connection.Uri.IsFile);
        string json = JsonSerializer.Serialize(servers);
        Assert.DoesNotContain(FakePlexServer.ServerToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain("shared-server-token-fixture", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selection_falls_back_past_unreachable_and_impostor_connections()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAsync();

        SelectedMediaServer? selected = await harness.Account.SelectServerAsync(FakePlexServer.MachineIdentifier);

        Assert.NotNull(selected);
        Assert.Equal(harness.Server.Address, selected.Connection.Uri);
        Assert.True(selected.Connection.Local);
        Assert.False(selected.Connection.Secure);
        FakePlexServer.RecordedRequest probe = Assert.Single(harness.Server.ImpostorRequests);
        Assert.Equal("/identity", probe.Path);
        Assert.Null(probe.Token);
        Assert.DoesNotContain(harness.Server.Requests, request => request.Path == "/identity" && request.Token is not null);
        Assert.Equal(new InMemoryAppSettings.StoredValue(FakePlexServer.ServerToken, true), harness.Settings.Values["plex.server_token"]);
        Assert.Equal(harness.Server.Address.ToString(), harness.Settings.Values["plex.server_url"].Value);

        MediaServerAccountStatus status = await harness.Account.GetStatusAsync();
        Assert.Equal(FakePlexServer.MachineIdentifier, status.Server?.Id);
        Assert.Equal("Living Room Server", status.Server?.Name);
    }

    [Fact]
    public async Task Server_that_requires_https_is_not_reached_over_http()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAsync();

        await Assert.ThrowsAsync<MediaServerException>(() => harness.Account.SelectServerAsync(FakePlexServer.SharedMachineIdentifier));

        Assert.DoesNotContain(harness.Server.Requests, request => request.Path == "/identity");
        Assert.False(harness.Settings.Values.ContainsKey("plex.server_id"));
    }

    [Fact]
    public async Task Unknown_server_and_non_server_devices_are_not_selected()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAsync();

        Assert.Null(await harness.Account.SelectServerAsync("no-such-server"));
        Assert.Null(await harness.Account.SelectServerAsync("phone-fixture"));
    }

    [Fact]
    public async Task Stored_connection_that_stops_answering_is_replaced()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        await harness.Settings.SetAsync("plex.server_url", harness.Server.RemoteHttpAddress.ToString(), false);

        MediaServerTestResult result = await harness.Media.TestConnectionAsync();

        Assert.True(result.Valid);
        Assert.Equal(harness.Server.Address.ToString(), harness.Settings.Values["plex.server_url"].Value);
    }

    [Fact]
    public async Task Stored_address_that_is_not_a_plain_base_address_is_refused()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        await harness.Settings.SetAsync("plex.server_url", "http://169.254.169.254/latest/meta-data", false);
        int before = harness.Server.Requests.Count;

        await Assert.ThrowsAsync<MediaServerNotConfiguredException>(() => harness.Media.GetLibrariesAsync());

        Assert.Equal(before, harness.Server.Requests.Count);
    }

    private static PlexResource Resource(bool httpsRequired, params PlexResourceConnection[] connections) =>
        new("Server", "machine", "server", "1.0", true, "token", httpsRequired, connections);

    private static PlexResourceConnection Connection(string protocol, string uri, bool local, bool relay = false, bool ipv6 = false) =>
        new(protocol, uri, local, relay, ipv6);
}
