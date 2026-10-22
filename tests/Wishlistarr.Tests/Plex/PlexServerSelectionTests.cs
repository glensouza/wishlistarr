using System.Text.Json;
using System.Text.Json.Nodes;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Plex;
using Wishlistarr.Tests.Jobs;

namespace Wishlistarr.Tests.Plex;

public sealed class PlexServerSelectionTests
{
    [Fact]
    public void Connections_are_ordered_local_https_first_and_plain_remote_http_last()
    {
        PlexResource server = Resource(
            false,
            Connection("http://203.0.113.10:32400", local: false),
            Connection("https://relay.plex.direct:8443", local: false, relay: true),
            Connection("http://192.168.1.20:32400", local: true),
            Connection("https://203-0-113-10.abc.plex.direct:32400", local: false),
            Connection("https://fd00--20.abc.plex.direct:32400", local: true, ipv6: true),
            Connection("https://192-168-1-20.abc.plex.direct:32400", local: true));

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
            Connection("http://192.168.1.20:32400", local: true),
            Connection("https://203-0-113-10.abc.plex.direct:32400", local: false));

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
    public async Task Shared_server_without_its_own_token_never_receives_the_account_token()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        JsonArray resources = JsonNode.Parse(harness.Server.ResourcesJson)!.AsArray();
        resources[0]!["accessToken"] = null;
        resources[0]!["owned"] = false;
        harness.Server.ResourcesJson = resources.ToJsonString();
        await harness.LinkAsync();

        await Assert.ThrowsAsync<MediaServerException>(() => harness.Account.SelectServerAsync(FakePlexServer.MachineIdentifier));

        Assert.DoesNotContain(harness.Server.Requests, request => !request.Path.StartsWith("/api/", StringComparison.Ordinal));
        Assert.Empty(harness.Server.ImpostorRequests);
        Assert.False(harness.Settings.Values.ContainsKey("plex.server_token"));
    }

    [Fact]
    public async Task Owned_server_without_its_own_token_uses_the_account_token()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        JsonArray resources = JsonNode.Parse(harness.Server.ResourcesJson)!.AsArray();
        resources[0]!["accessToken"] = null;
        harness.Server.ResourcesJson = resources.ToJsonString();
        await harness.LinkAsync();

        Assert.NotNull(await harness.Account.SelectServerAsync(FakePlexServer.MachineIdentifier));

        Assert.Equal(FakePlexServer.AccountToken, harness.Settings.Values["plex.server_token"].Value);
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
    public async Task Repeated_network_failures_wait_before_selecting_a_server_again()
    {
        ManualTime time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        await using PlexHarness harness = await PlexHarness.CreateAsync(time: time);
        await harness.LinkAndSelectAsync();
        string original = harness.Server.ResourcesJson;
        await PointAtClosedConnectionsAsync(harness);
        int baseline = harness.Server.Count("/api/v2/resources");

        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());
        Assert.Equal(baseline + 1, harness.Server.Count("/api/v2/resources"));

        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Media.GetLibrariesAsync());
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Media.GetLibrariesAsync());
        Assert.Equal(baseline + 1, harness.Server.Count("/api/v2/resources"));

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());
        Assert.Equal(baseline + 2, harness.Server.Count("/api/v2/resources"));

        time.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Media.GetLibrariesAsync());
        Assert.Equal(baseline + 2, harness.Server.Count("/api/v2/resources"));

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());
        Assert.Equal(baseline + 3, harness.Server.Count("/api/v2/resources"));

        harness.Server.ResourcesJson = original;
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await harness.Media.TestConnectionAsync()).Valid);

        await harness.Settings.SetAsync("plex.server_url", harness.Server.RemoteHttpAddress.ToString(), false);
        int afterRecovery = harness.Server.Count("/api/v2/resources");
        Assert.True((await harness.Media.TestConnectionAsync()).Valid);
        Assert.Equal(afterRecovery + 1, harness.Server.Count("/api/v2/resources"));
    }

    [Fact]
    public async Task A_successful_selection_restarts_the_wait_from_the_first_step()
    {
        ManualTime time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        await using PlexHarness harness = await PlexHarness.CreateAsync(time: time);
        await harness.LinkAndSelectAsync();
        string original = harness.Server.ResourcesJson;
        await PointAtClosedConnectionsAsync(harness);

        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());
        time.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());

        harness.Server.ResourcesJson = original;
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.True((await harness.Media.TestConnectionAsync()).Valid);

        await PointAtClosedConnectionsAsync(harness);
        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());
        int afterFailure = harness.Server.Count("/api/v2/resources");

        time.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());
        Assert.Equal(afterFailure + 1, harness.Server.Count("/api/v2/resources"));
    }

    [Fact]
    public async Task Choosing_a_server_clears_the_wait()
    {
        ManualTime time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        await using PlexHarness harness = await PlexHarness.CreateAsync(time: time);
        await harness.LinkAndSelectAsync();
        string original = harness.Server.ResourcesJson;
        await PointAtClosedConnectionsAsync(harness);
        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Media.GetLibrariesAsync());

        harness.Server.ResourcesJson = original;
        Assert.NotNull(await harness.Account.SelectServerAsync(FakePlexServer.MachineIdentifier));
        await PointAtClosedConnectionsAsync(harness);
        int before = harness.Server.Count("/api/v2/resources");

        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());

        Assert.Equal(before + 1, harness.Server.Count("/api/v2/resources"));
    }

    [Fact]
    public async Task Concurrent_requests_that_fail_together_select_a_server_once()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        await harness.Settings.SetAsync("plex.server_url", harness.Server.RemoteHttpAddress.ToString(), false);
        int baseline = harness.Server.Count("/api/v2/resources");

        IReadOnlyList<MediaLibrary>[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => harness.Media.GetLibrariesAsync()));

        Assert.All(results, libraries => Assert.NotEmpty(libraries));
        Assert.Equal(baseline + 1, harness.Server.Count("/api/v2/resources"));
    }

    [Fact]
    public async Task Selection_backoff_is_capped_at_one_minute()
    {
        ManualTime time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        await using PlexHarness harness = await PlexHarness.CreateAsync(time: time);
        await harness.LinkAndSelectAsync();
        await PointAtClosedConnectionsAsync(harness);

        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());
        for (int failure = 0; failure < 5; failure++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());
        }

        int capped = harness.Server.Count("/api/v2/resources");
        time.Advance(TimeSpan.FromSeconds(59));
        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Media.GetLibrariesAsync());
        Assert.Equal(capped, harness.Server.Count("/api/v2/resources"));
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLibrariesAsync());
        Assert.Equal(capped + 1, harness.Server.Count("/api/v2/resources"));
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

    private static async Task PointAtClosedConnectionsAsync(PlexHarness harness)
    {
        JsonArray resources = JsonNode.Parse(harness.Server.ResourcesJson)!.AsArray();
        JsonArray connections = resources[0]!["connections"]!.AsArray();
        connections.Clear();
        connections.Add(new JsonObject
        {
            ["protocol"] = "http",
            ["address"] = harness.Server.RemoteHttpAddress.Host,
            ["port"] = harness.Server.RemoteHttpAddress.Port,
            ["uri"] = harness.Server.RemoteHttpAddress.ToString(),
            ["local"] = false,
            ["relay"] = false,
            ["IPv6"] = false,
        });
        harness.Server.ResourcesJson = resources.ToJsonString();
        await harness.Settings.SetAsync("plex.server_url", harness.Server.RemoteHttpAddress.ToString(), false);
    }

    private static PlexResource Resource(bool httpsRequired, params PlexResourceConnection[] connections) =>
        new("Server", "machine", "server", "1.0", true, "token", httpsRequired, connections);

    private static PlexResourceConnection Connection(string uri, bool local, bool relay = false, bool ipv6 = false) =>
        new(uri, local, relay, ipv6);
}
