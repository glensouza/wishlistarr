using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Tests.Plex;

public sealed class PlexScannerTests
{
    private const long FixtureUpdatedAt = 1727740800;

    [Fact]
    public async Task A_scan_reads_every_movie_and_show_with_ids_but_without_labels()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync(options => options.PageSize = 2);
        await harness.LinkAndSelectAsync();
        harness.Server.Item("101").SetLabels("Kids-Ada", "Favourite");
        ILibraryScanner scanner = harness.Provider.GetRequiredService<ILibraryScanner>();

        LibraryScan scan = await scanner.ScanAsync(null);
        List<LibraryEntry> entries = await ReadAsync(scan);

        Assert.Equal(FakePlexServer.MachineIdentifier, scan.ServerId);
        Assert.Equal(["101", "102", "103", "201", "202", "301", "302", "401", "402"], entries.Select(entry => entry.Item.Id).Order());
        LibraryEntry matrix = entries.Single(entry => entry.Item.Id == "101");
        Assert.Null(matrix.Labels);
        Assert.Contains(new ExternalId(ExternalIdSource.Tmdb, "603"), matrix.Item.ExternalIds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(FixtureUpdatedAt), matrix.UpdatedAt);
        Assert.Equal(MediaKind.Show, entries.Single(entry => entry.Item.Id == "401").Item.Kind);
        Assert.Equal([new ExternalId(ExternalIdSource.Tvdb, "74852")], entries.Single(entry => entry.Item.Id == "401").Item.ExternalIds);
        Assert.Equal([new ExternalId(ExternalIdSource.Imdb, "tt0111161")], entries.Single(entry => entry.Item.Id == "301").Item.ExternalIds);
        Assert.Empty(entries.Single(entry => entry.Item.Id == "402").Item.ExternalIds);
    }

    [Fact]
    public async Task Labels_are_read_per_label_from_filtered_listings_without_fetching_each_item()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync(options => options.PageSize = 1);
        await harness.LinkAndSelectAsync();
        harness.Server.Item("101").SetLabels("Kids-Ada", "Favourite");
        harness.Server.Item("102").SetLabels("Kids-Ada");
        harness.Server.Item("201").SetLabels("kids-ada");
        ILibraryScanner scanner = harness.Provider.GetRequiredService<ILibraryScanner>();

        LibraryLabelScan scan = await scanner.ScanLabelsAsync();

        Assert.True(scan.Complete);
        Assert.Equal(FakePlexServer.MachineIdentifier, scan.ServerId);
        Assert.Equal(["Favourite", "Kids-Ada"], scan.LabelsByItem["101"].Order());
        Assert.Equal(["Kids-Ada"], scan.LabelsByItem["102"]);
        Assert.Equal(["kids-ada"], scan.LabelsByItem["201"]);
        Assert.Equal(3, scan.LabelsByItem.Count);
        Assert.DoesNotContain(harness.Server.Requests, request => request.Path.StartsWith("/library/metadata/", StringComparison.Ordinal));
        Assert.Equal(4, harness.Server.Requests.Count(request => request.Path == "/library/sections/1/label" || request.Path == "/library/sections/2/label" || request.Path == "/library/sections/3/label" || request.Path == "/library/sections/4/label"));
    }

    [Fact]
    public async Task Listings_never_carry_labels_so_only_the_item_detail_does()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        harness.Server.Item("101").SetLabels("Kids-Ada");
        ILibraryScanner scanner = harness.Provider.GetRequiredService<ILibraryScanner>();

        List<LibraryEntry> listed = await ReadAsync(await scanner.ScanAsync(null));
        LibrarySnapshot? detail = await scanner.GetEntryAsync("101");

        Assert.All(listed, entry => Assert.Null(entry.Labels));
        Assert.Equal(["Kids-Ada"], detail?.Entry.Labels);
    }

    [Fact]
    public async Task A_scan_with_a_cursor_asks_plex_only_for_items_changed_since_then()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        ILibraryScanner scanner = harness.Provider.GetRequiredService<ILibraryScanner>();

        List<LibraryEntry> unchanged = await ReadAsync(await scanner.ScanAsync(DateTimeOffset.FromUnixTimeSeconds(FixtureUpdatedAt)));
        List<LibraryEntry> changed = await ReadAsync(await scanner.ScanAsync(DateTimeOffset.FromUnixTimeSeconds(FixtureUpdatedAt - 1)));

        Assert.Empty(unchanged);
        Assert.Equal(9, changed.Count);
        Assert.Contains(
            harness.Server.Requests,
            request => request.Path.EndsWith("/all", StringComparison.Ordinal)
                && request.Query.GetValueOrDefault("updatedAt>>") == (FixtureUpdatedAt - 1).ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_scan_reports_when_plex_returned_fewer_items_than_it_announced()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync(options => options.PageSize = 2);
        await harness.LinkAndSelectAsync();
        ILibraryScanner scanner = harness.Provider.GetRequiredService<ILibraryScanner>();

        LibraryScan complete = await scanner.ScanAsync(null);
        await ReadAsync(complete);
        harness.Server.HiddenKeys["102"] = true;
        LibraryScan partial = await scanner.ScanAsync(null);
        List<LibraryEntry> entries = await ReadAsync(partial);

        Assert.True(complete.Report.Complete);
        Assert.False(partial.Report.Complete);
        Assert.DoesNotContain(entries, entry => entry.Item.Id == "102");
    }

    [Fact]
    public async Task A_single_item_is_read_by_rating_key_and_episodes_resolve_to_their_show()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        harness.Server.Item("201").SetLabels("Kids-Ada");
        ILibraryScanner scanner = harness.Provider.GetRequiredService<ILibraryScanner>();

        LibrarySnapshot? episode = await scanner.GetEntryAsync("20111");
        LibrarySnapshot? movie = await scanner.GetEntryAsync("101");

        Assert.NotNull(episode);
        Assert.Equal(("201", MediaKind.Show, "2"), (episode.Entry.Item.Id, episode.Entry.Item.Kind, episode.Entry.Item.LibraryId));
        Assert.Equal(["Kids-Ada"], episode.Entry.Labels);
        Assert.Equal(FakePlexServer.MachineIdentifier, episode.ServerId);
        Assert.Equal("101", movie?.Entry.Item.Id);
    }

    [Fact]
    public async Task Items_that_are_not_movies_or_shows_and_unknown_items_are_not_indexed()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        ILibraryScanner scanner = harness.Provider.GetRequiredService<ILibraryScanner>();

        Assert.Null(await scanner.GetEntryAsync("501"));
        Assert.Null(await scanner.GetEntryAsync("999999"));
        Assert.Null(await scanner.GetEntryAsync("../1"));
    }

    [Fact]
    public async Task Scanning_without_a_selected_server_reports_that_plex_is_not_configured()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        ILibraryScanner scanner = harness.Provider.GetRequiredService<ILibraryScanner>();

        await Assert.ThrowsAsync<MediaServerNotConfiguredException>(() => scanner.ScanAsync(null));
    }

    private static async Task<List<LibraryEntry>> ReadAsync(LibraryScan scan)
    {
        List<LibraryEntry> entries = [];
        await foreach (LibraryEntry entry in scan.Entries)
        {
            entries.Add(entry);
        }

        return entries;
    }
}
