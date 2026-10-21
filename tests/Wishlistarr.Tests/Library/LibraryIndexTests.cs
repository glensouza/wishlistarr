using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Tests.Library;

public sealed class LibraryIndexTests
{
    private static readonly DateTimeOffset Early = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ExternalId Tmdb(string value) => new(ExternalIdSource.Tmdb, value);

    private static ExternalId Tvdb(string value) => new(ExternalIdSource.Tvdb, value);

    private static ExternalId Imdb(string value) => new(ExternalIdSource.Imdb, value);

    [Fact]
    public async Task The_index_cannot_answer_before_the_first_full_refresh()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "The Matrix", [Tmdb("603")]));

        Assert.Null(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "603"));

        await harness.FullAsync();

        Assert.NotNull(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "603"));
    }

    [Fact]
    public async Task Movies_and_shows_are_found_by_tmdb_id_and_kinds_do_not_collide()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "The Matrix", [Tmdb("603"), Imdb("tt0133093")]));
        harness.Scanner.Entries.Add(FakeScanner.Entry("201", MediaKind.Show, "Bluey", [Tmdb("603"), Tvdb("353546")]));
        harness.Scanner.Entries.Add(FakeScanner.Entry("202", MediaKind.Show, "Other", [Tmdb("604")]));

        await harness.FullAsync();

        MediaItem movie = Assert.Single(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "603") ?? []);
        MediaItem show = Assert.Single(await harness.FindAsync(MediaKind.Show, ExternalIdSource.Tmdb, "603") ?? []);
        Assert.Equal(("101", "The Matrix", MediaKind.Movie), (movie.Id, movie.Title, movie.Kind));
        Assert.Equal(("201", "Bluey", MediaKind.Show), (show.Id, show.Title, show.Kind));
        Assert.Empty(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "604") ?? throw new InvalidOperationException());
        Assert.Equal("201", Assert.Single(await harness.FindAsync(MediaKind.Show, ExternalIdSource.Tvdb, "353546") ?? []).Id);
        Assert.Equal("101", Assert.Single(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Imdb, "tt0133093") ?? []).Id);
    }

    [Fact]
    public async Task Items_sharing_a_title_resolve_to_one_canonical_record()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "The Matrix", [Tmdb("603")]));
        harness.Scanner.Entries.Add(FakeScanner.Entry("301", MediaKind.Movie, "The Matrix (4K)", [Tmdb("603")]));

        await harness.FullAsync();

        IReadOnlyList<MediaItem> found = await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "603") ?? [];
        Assert.Equal(["101", "301"], found.Select(item => item.Id));
        await harness.InScopeAsync(async services =>
        {
            WishlistarrDbContext db = services.GetRequiredService<WishlistarrDbContext>();
            LibraryTitle title = await db.LibraryTitles.SingleAsync();
            Assert.Equal((MediaKind.Movie, 603), (title.Kind, title.TmdbId));
            return 0;
        });
    }

    [Fact]
    public async Task The_external_id_map_holds_every_id_the_plex_item_carries()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry(
            "101",
            MediaKind.Movie,
            "The Matrix",
            [Tmdb("603"), Tvdb("169"), Imdb("tt0133093")],
            guid: "plex://movie/5d7768ba96b655001fdc0408"));

        await harness.FullAsync();

        LibraryItem item = Assert.Single(await harness.GetItemsAsync());
        Dictionary<LibraryIdSource, string> map = item.ExternalIds.ToDictionary(id => id.Source, id => id.Value);
        Assert.Equal(
            new Dictionary<LibraryIdSource, string>
            {
                [LibraryIdSource.Tmdb] = "603",
                [LibraryIdSource.Tvdb] = "169",
                [LibraryIdSource.Imdb] = "tt0133093",
                [LibraryIdSource.PlexGuid] = "plex://movie/5d7768ba96b655001fdc0408",
                [LibraryIdSource.PlexRatingKey] = "101",
            },
            map);
        Assert.All(item.ExternalIds, id => Assert.Equal(LibraryIdOrigin.Plex, id.Origin));
    }

    [Fact]
    public async Task Legacy_items_are_resolved_to_tmdb_through_tvdb_and_imdb()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("301", MediaKind.Movie, "The Shawshank Redemption", [Imdb("tt0111161")], guid: "com.plexapp.agents.imdb://tt0111161?lang=en"));
        harness.Scanner.Entries.Add(FakeScanner.Entry("401", MediaKind.Show, "Avatar", [Tvdb("74852")], guid: "com.plexapp.agents.thetvdb://74852?lang=en"));
        harness.Scanner.Entries.Add(FakeScanner.Entry("402", MediaKind.Show, "Unmatched", []));
        harness.Tmdb.Known[(TmdbMediaType.Movie, ExternalIdSource.Imdb, "tt0111161")] = 278;
        harness.Tmdb.Known[(TmdbMediaType.Tv, ExternalIdSource.Tvdb, "74852")] = 246;

        await harness.FullAsync();

        Assert.Equal("301", Assert.Single(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "278") ?? []).Id);
        Assert.Equal("401", Assert.Single(await harness.FindAsync(MediaKind.Show, ExternalIdSource.Tmdb, "246") ?? []).Id);
        List<LibraryItem> items = await harness.GetItemsAsync();
        LibraryExternalId resolved = Assert.Single(items.Single(item => item.RatingKey == "301").ExternalIds, id => id.Source == LibraryIdSource.Tmdb);
        Assert.Equal(LibraryIdOrigin.TmdbLookup, resolved.Origin);
        Assert.Equal(harness.Time.GetUtcNow(), resolved.FetchedAt);
        Assert.Null(items.Single(item => item.RatingKey == "402").TitleId);
    }

    [Fact]
    public async Task A_legacy_item_is_not_looked_up_again_once_resolved()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("301", MediaKind.Movie, "Resolved", [Imdb("tt0111161")], updatedAt: Early));
        harness.Scanner.Entries.Add(FakeScanner.Entry("302", MediaKind.Movie, "Late", [Imdb("tt0000002")], updatedAt: Early));
        harness.Tmdb.Known[(TmdbMediaType.Movie, ExternalIdSource.Imdb, "tt0111161")] = 278;

        await harness.FullAsync();
        harness.Tmdb.Calls.Clear();
        harness.Tmdb.Known[(TmdbMediaType.Movie, ExternalIdSource.Imdb, "tt0000002")] = 5;
        await harness.FullAsync();

        Assert.Equal([(TmdbMediaType.Movie, ExternalIdSource.Imdb, "tt0000002")], harness.Tmdb.Calls);
        Assert.Equal(2, (await harness.GetItemsAsync()).Count(item => item.TitleId is not null));
    }

    [Fact]
    public async Task A_failed_tmdb_lookup_does_not_stop_the_refresh()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("301", MediaKind.Movie, "A", [Imdb("tt0000001")]));
        harness.Scanner.Entries.Add(FakeScanner.Entry("302", MediaKind.Movie, "B", [Tmdb("603")]));
        harness.Tmdb.Failure = new TmdbException("TMDB answered 500.");

        await harness.FullAsync();

        List<LibraryItem> items = await harness.GetItemsAsync();
        Assert.Equal(2, items.Count);
        Assert.Null(items.Single(item => item.RatingKey == "301").TitleId);
        Assert.NotNull(items.Single(item => item.RatingKey == "302").TitleId);
    }

    [Fact]
    public async Task Without_a_tmdb_key_lookups_are_skipped_after_the_first_attempt()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("301", MediaKind.Movie, "A", [Imdb("tt0000001")]));
        harness.Scanner.Entries.Add(FakeScanner.Entry("302", MediaKind.Movie, "B", [Imdb("tt0000002")]));
        harness.Tmdb.Failure = new TmdbNotConfiguredException();

        await harness.FullAsync();

        Assert.Single(harness.Tmdb.Calls);
        Assert.All(await harness.GetItemsAsync(), item => Assert.Null(item.TitleId));
    }

    [Fact]
    public async Task Every_label_on_an_item_is_recorded_and_follows_changes()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "The Matrix", [Tmdb("603")], ["Kids-Ada", "Favourite"], Early));

        await harness.FullAsync();

        Assert.Equal(["Favourite", "Kids-Ada"], (await harness.GetItemsAsync()).Single().Labels.Select(label => label.Label).Order());

        harness.Scanner.Entries.Clear();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "The Matrix", [Tmdb("603")], ["kids-ada", "Weekend"], Early.AddHours(1)));
        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.IncrementalAsync();

        Assert.Equal(["Kids-Ada", "Weekend"], (await harness.GetItemsAsync()).Single().Labels.Select(label => label.Label).Order());
        await harness.InScopeAsync(async services =>
        {
            WishlistarrDbContext db = services.GetRequiredService<WishlistarrDbContext>();
            Assert.Equal(2, await db.LibraryItemLabels.CountAsync(label => label.Label == "KIDS-ADA" || label.Label == "weekend"));
            return 0;
        });
    }

    [Fact]
    public async Task An_incremental_refresh_reads_only_items_changed_since_the_cursor()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "Old", [Tmdb("1")], updatedAt: Early));
        await harness.FullAsync();

        harness.Scanner.Entries.Add(FakeScanner.Entry("102", MediaKind.Movie, "New", [Tmdb("2")], updatedAt: Early.AddDays(1)));
        harness.Time.Advance(TimeSpan.FromDays(1));
        await harness.IncrementalAsync();

        Assert.Equal<DateTimeOffset?>([null, Early - TimeSpan.FromSeconds(1)], harness.Scanner.Scans);
        Assert.Equal("102", Assert.Single(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "2") ?? []).Id);
        Assert.Equal(2, (await harness.GetItemsAsync()).Count);
    }

    [Fact]
    public async Task An_incremental_refresh_before_any_full_refresh_does_a_full_one()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "Old", [Tmdb("1")], updatedAt: Early));

        await harness.IncrementalAsync();

        Assert.Equal<DateTimeOffset?>([null], harness.Scanner.Scans);
        Assert.NotNull(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "1"));
    }

    [Fact]
    public async Task A_full_refresh_drops_removed_items_other_servers_and_unused_titles()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "Keep", [Tmdb("1")]));
        harness.Scanner.Entries.Add(FakeScanner.Entry("102", MediaKind.Movie, "Remove", [Tmdb("2")]));
        await harness.FullAsync();

        harness.Scanner.Entries.RemoveAt(1);
        await harness.FullAsync();

        Assert.Empty(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "2") ?? throw new InvalidOperationException());
        harness.Scanner.ServerId = "server-b";
        await harness.FullAsync();

        LibraryItem item = Assert.Single(await harness.GetItemsAsync());
        Assert.Equal("server-b", item.ServerId);
        await harness.InScopeAsync(async services =>
        {
            Assert.Equal(1, await services.GetRequiredService<WishlistarrDbContext>().LibraryTitles.CountAsync());
            return 0;
        });
    }

    [Fact]
    public async Task Changing_the_plex_server_forces_a_full_refresh_on_the_next_incremental_run()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "A", [Tmdb("1")], updatedAt: Early));
        await harness.FullAsync();

        harness.Scanner.ServerId = "server-b";
        await harness.IncrementalAsync();

        Assert.Equal("server-b", Assert.Single(await harness.GetItemsAsync()).ServerId);
    }

    [Theory]
    [InlineData(ExternalIdSource.Tmdb, "603", "IX_LibraryTitles_Kind_TmdbId")]
    [InlineData(ExternalIdSource.Tvdb, "74852", "IX_LibraryExternalIds_Source_Value")]
    [InlineData(ExternalIdSource.Imdb, "tt0133093", "IX_LibraryExternalIds_Source_Value")]
    public async Task Lookups_run_the_generated_query_through_an_index(ExternalIdSource source, string value, string index)
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "The Matrix", [Tmdb("603"), Imdb("tt0133093"), Tvdb("74852")]));
        await harness.FullAsync();

        Assert.Single(await harness.FindAsync(MediaKind.Movie, source, value) ?? []);
        string plan = await QueryPlanAsync(harness, harness.Sql.Statements[^1]);

        Assert.Contains(index, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rows_of_another_server_are_not_returned_and_a_changed_selection_makes_the_index_unready()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "The Matrix", [Tmdb("603")]));
        await harness.FullAsync();
        await harness.InScopeAsync(async services =>
        {
            WishlistarrDbContext db = services.GetRequiredService<WishlistarrDbContext>();
            LibraryItem stale = await db.LibraryItems.Include(item => item.ExternalIds).SingleAsync();
            db.LibraryItems.Add(new LibraryItem
            {
                ServerId = "server-old",
                RatingKey = "999",
                Guid = "plex://movie/old",
                LibraryId = "1",
                Title = "Old server copy",
                Kind = MediaKind.Movie,
                TitleId = stale.TitleId,
            });
            await db.SaveChangesAsync();
            return 0;
        });

        IReadOnlyList<MediaItem> found = await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "603") ?? [];
        harness.Scanner.SelectedServerId = "server-b";

        Assert.Equal("101", Assert.Single(found).Id);
        Assert.Null(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "603"));
    }

    [Fact]
    public async Task A_single_item_from_a_server_other_than_the_indexed_one_is_ignored()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "The Matrix", [Tmdb("603")]));
        await harness.FullAsync();

        harness.Scanner.ServerId = "server-b";
        harness.Scanner.Entries.Add(FakeScanner.Entry("102", MediaKind.Movie, "Elsewhere", [Tmdb("129")]));
        Assert.True(await harness.ItemAsync("102"));

        Assert.Equal("101", Assert.Single(await harness.GetItemsAsync()).RatingKey);
    }


    [Fact]
    public async Task Tmdb_derived_ids_carry_a_fetched_at_timestamp()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("301", MediaKind.Movie, "Legacy", [Imdb("tt0111161")]));
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "Modern", [Tmdb("603")]));
        harness.Tmdb.Known[(TmdbMediaType.Movie, ExternalIdSource.Imdb, "tt0111161")] = 278;

        await harness.FullAsync();

        List<LibraryExternalId> derived = [.. (await harness.GetItemsAsync())
            .SelectMany(item => item.ExternalIds)
            .Where(id => id.Origin == LibraryIdOrigin.TmdbLookup)];
        Assert.NotNull(Assert.Single(derived).FetchedAt);
    }

    [Fact]
    public async Task A_single_item_that_plex_no_longer_has_is_removed_with_its_title()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "Keep", [Tmdb("1")]));
        harness.Scanner.Entries.Add(FakeScanner.Entry("102", MediaKind.Movie, "Gone", [Tmdb("2")]));
        await harness.FullAsync();

        harness.Scanner.Entries.RemoveAt(1);
        Assert.True(await harness.ItemAsync("102"));

        Assert.Equal("101", Assert.Single(await harness.GetItemsAsync()).RatingKey);
        Assert.Empty(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "2") ?? throw new InvalidOperationException());
        await harness.InScopeAsync(async services =>
        {
            Assert.Equal(1, await services.GetRequiredService<WishlistarrDbContext>().LibraryTitles.CountAsync());
            return 0;
        });
    }

    [Fact]
    public async Task A_looked_up_tmdb_id_is_dropped_when_plex_starts_reporting_its_own()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("301", MediaKind.Movie, "Legacy", [Imdb("tt0111161")], updatedAt: Early));
        harness.Tmdb.Known[(TmdbMediaType.Movie, ExternalIdSource.Imdb, "tt0111161")] = 278;
        await harness.FullAsync();

        harness.Scanner.Entries.Clear();
        harness.Scanner.Entries.Add(FakeScanner.Entry("301", MediaKind.Movie, "Legacy", [Imdb("tt0111161"), Tmdb("999")], updatedAt: Early.AddHours(1)));
        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.IncrementalAsync();

        LibraryItem item = Assert.Single(await harness.GetItemsAsync());
        LibraryExternalId tmdb = Assert.Single(item.ExternalIds, id => id.Source == LibraryIdSource.Tmdb);
        Assert.Equal(("999", LibraryIdOrigin.Plex), (tmdb.Value, tmdb.Origin));
        Assert.Empty(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "278") ?? throw new InvalidOperationException());
        Assert.Single(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "999") ?? []);
    }

    [Fact]
    public async Task A_looked_up_tmdb_id_is_resolved_again_when_plex_changes_the_legacy_id()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("401", MediaKind.Show, "Rematched", [Tvdb("100")], updatedAt: Early));
        harness.Tmdb.Known[(TmdbMediaType.Tv, ExternalIdSource.Tvdb, "100")] = 1;
        harness.Tmdb.Known[(TmdbMediaType.Tv, ExternalIdSource.Tvdb, "200")] = 2;
        await harness.FullAsync();

        harness.Scanner.Entries.Clear();
        harness.Scanner.Entries.Add(FakeScanner.Entry("401", MediaKind.Show, "Rematched", [Tvdb("200")], updatedAt: Early.AddHours(1)));
        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.IncrementalAsync();

        Assert.Empty(await harness.FindAsync(MediaKind.Show, ExternalIdSource.Tmdb, "1") ?? throw new InvalidOperationException());
        Assert.Equal("401", Assert.Single(await harness.FindAsync(MediaKind.Show, ExternalIdSource.Tmdb, "2") ?? []).Id);
    }

    [Fact]
    public async Task An_item_updated_in_the_same_second_as_the_cursor_is_still_picked_up()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "First", [Tmdb("1")], updatedAt: Early));
        await harness.FullAsync();

        harness.Scanner.Entries.Add(FakeScanner.Entry("102", MediaKind.Movie, "Same second", [Tmdb("2")], updatedAt: Early));
        await harness.IncrementalAsync();

        Assert.Equal("102", Assert.Single(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "2") ?? []).Id);
    }

    [Fact]
    public async Task An_incomplete_full_scan_does_not_prune_or_mark_the_index_ready()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "One", [Tmdb("1")]));
        harness.Scanner.Entries.Add(FakeScanner.Entry("102", MediaKind.Movie, "Two", [Tmdb("2")]));
        harness.Scanner.Complete = false;
        await harness.FullAsync();
        Assert.Null(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "1"));

        harness.Scanner.Complete = true;
        await harness.FullAsync();
        harness.Scanner.Entries.RemoveAt(1);
        harness.Scanner.Complete = false;
        await harness.FullAsync();

        Assert.Equal(["101", "102"], (await harness.GetItemsAsync()).Select(item => item.RatingKey));
    }

    [Fact]
    public async Task A_full_scan_that_returns_nothing_does_not_empty_a_populated_index()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        harness.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "One", [Tmdb("1")]));
        await harness.FullAsync();

        harness.Scanner.Entries.Clear();
        await harness.FullAsync();

        Assert.Single(await harness.GetItemsAsync());
        Assert.NotNull(await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "1"));
    }

    [Fact]
    public async Task Large_refreshes_span_batches_and_share_titles_between_them()
    {
        await using LibraryHarness harness = await LibraryHarness.CreateAsync();
        for (int number = 0; number < 450; number++)
        {
            harness.Scanner.Entries.Add(FakeScanner.Entry($"{1000 + number}", MediaKind.Movie, $"Movie {number}", [Tmdb($"{1 + (number % 300)}")], ["Kids-Ada"]));
        }

        await harness.FullAsync();

        Assert.Equal(450, (await harness.GetItemsAsync()).Count);
        Assert.Equal(2, (await harness.FindAsync(MediaKind.Movie, ExternalIdSource.Tmdb, "1") ?? []).Count);
        await harness.InScopeAsync(async services =>
        {
            Assert.Equal(300, await services.GetRequiredService<WishlistarrDbContext>().LibraryTitles.CountAsync());
            return 0;
        });
    }

    private static async Task<string> QueryPlanAsync(LibraryHarness harness, string sql)
    {
        await using SqliteConnection connection = new($"Data Source={harness.DatabasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        List<string> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(3));
        }

        return string.Join('\n', rows);
    }
}
