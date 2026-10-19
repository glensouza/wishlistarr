using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Tests.Plex;

public sealed class PlexLinkTests
{
    [Fact]
    public async Task Link_flow_saves_the_token_without_it_being_typed()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();

        MediaServerLinkStart link = await harness.Account.StartLinkAsync();
        MediaServerLinkCheck? pending = await harness.Account.CheckLinkAsync(link.Id);
        harness.Server.PinApproved = true;
        MediaServerLinkCheck? linked = await harness.Account.CheckLinkAsync(link.Id);

        string clientIdentifier = harness.Settings.Values["plex.client_identifier"].Value;
        Assert.Equal(FakePlexServer.PinCode, link.Code);
        Assert.Equal("app.plex.tv", link.AuthUrl.Host);
        Assert.Contains($"clientID={clientIdentifier}", link.AuthUrl.Fragment, StringComparison.Ordinal);
        Assert.Contains($"code={FakePlexServer.PinCode}", link.AuthUrl.Fragment, StringComparison.Ordinal);
        Assert.True(link.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.Equal(MediaServerLinkState.Pending, pending?.State);
        Assert.Equal(MediaServerLinkState.Linked, linked?.State);
        Assert.Equal(FakePlexServer.AccountName, linked?.AccountName);
        Assert.Equal(new InMemoryAppSettings.StoredValue(FakePlexServer.AccountToken, true), harness.Settings.Values["plex.token"]);
        Assert.Null(await harness.Account.CheckLinkAsync(link.Id));

        MediaServerAccountStatus status = await harness.Account.GetStatusAsync();
        Assert.True(status.Linked);
        Assert.Equal(FakePlexServer.AccountName, status.AccountName);
        Assert.Null(status.Server);
    }

    [Fact]
    public async Task Client_identifier_is_created_once_and_survives_a_restart()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();

        await harness.Account.StartLinkAsync();
        await harness.Account.StartLinkAsync();
        string first = (await harness.Account.GetStatusAsync()).ClientIdentifier;
        IMediaServerAccount restarted = harness.CreateProvider().GetRequiredService<IMediaServerAccount>();
        string afterRestart = (await restarted.GetStatusAsync()).ClientIdentifier;
        await restarted.StartLinkAsync();

        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal(first, afterRestart);
        Assert.Equal(first, harness.Settings.Values["plex.client_identifier"].Value);
        Assert.False(harness.Settings.Values["plex.client_identifier"].IsSecret);
        Assert.Equal(3, harness.Server.Count("/api/v2/pins"));
        Assert.All(harness.Server.Requests, request => Assert.Equal(first, request.ClientIdentifier));
    }

    [Fact]
    public async Task Concurrent_first_use_creates_a_single_client_identifier()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();

        MediaServerAccountStatus[] statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => harness.Account.GetStatusAsync()));

        Assert.Single(statuses.Select(status => status.ClientIdentifier).Distinct());
    }

    [Fact]
    public async Task Unknown_and_expired_links_are_reported()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();

        Assert.Null(await harness.Account.CheckLinkAsync("not-a-number"));
        Assert.Null(await harness.Account.CheckLinkAsync(FakePlexServer.PinId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Empty(harness.Server.Requests);

        MediaServerLinkStart link = await harness.Account.StartLinkAsync();
        harness.Server.PinGone = true;
        MediaServerLinkCheck? expired = await harness.Account.CheckLinkAsync(link.Id);

        Assert.Equal(MediaServerLinkState.Expired, expired?.State);
        Assert.Null(await harness.Account.CheckLinkAsync(link.Id));
        Assert.False(harness.Settings.Values.ContainsKey("plex.token"));
    }

    [Fact]
    public async Task Unlink_forgets_the_sign_in_and_server_but_keeps_the_client_identifier()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        string clientIdentifier = harness.Settings.Values["plex.client_identifier"].Value;

        await harness.Account.UnlinkAsync();

        MediaServerAccountStatus status = await harness.Account.GetStatusAsync();
        Assert.False(status.Linked);
        Assert.Null(status.Server);
        Assert.Equal(clientIdentifier, status.ClientIdentifier);
        Assert.Equal(["plex.client_identifier"], harness.Settings.Values.Keys);
        await Assert.ThrowsAsync<MediaServerNotConfiguredException>(() => harness.Media.GetLibrariesAsync());
    }

    [Fact]
    public async Task Calls_that_need_an_account_fail_clearly_before_linking()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();

        await Assert.ThrowsAsync<MediaServerNotConfiguredException>(() => harness.Account.GetServersAsync());
        await Assert.ThrowsAsync<MediaServerNotConfiguredException>(() => harness.Account.SelectServerAsync(FakePlexServer.MachineIdentifier));
        await Assert.ThrowsAsync<MediaServerNotConfiguredException>(() => harness.Media.GetUsersAsync());
        await Assert.ThrowsAsync<MediaServerNotConfiguredException>(() => harness.Media.GetLibrariesAsync());
        Assert.Empty(harness.Server.Requests);
    }
}
