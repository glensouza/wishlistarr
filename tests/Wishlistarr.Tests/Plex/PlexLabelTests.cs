using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Tests.Plex;

public sealed class PlexLabelTests
{
    [Fact]
    public async Task Labels_are_read_with_the_item_library_and_lock_state()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        harness.Server.Item("101").SetLabels("Favorites", "Ben");

        MediaItemLabels? item = await harness.Media.GetLabelsAsync("101");

        Assert.NotNull(item);
        Assert.Equal(FakePlexServer.MachineIdentifier, item.ServerId);
        Assert.Equal("101", item.Id);
        Assert.Equal("plex://movie/5d7768ba96b655001fdc0408", item.Guid);
        Assert.Equal("The Matrix", item.Title);
        Assert.Equal(MediaKind.Movie, item.Kind);
        Assert.Equal("1", item.LibraryId);
        Assert.Equal(["Favorites", "Ben"], item.Labels);
        Assert.False(item.Locked);
        Assert.Equal(
            [new ExternalId(ExternalIdSource.Imdb, "tt0133093"), new ExternalId(ExternalIdSource.Tmdb, "603"), new ExternalId(ExternalIdSource.Tvdb, "169")],
            item.ExternalIds);
        Assert.All(
            harness.Server.Requests.Where(request => request.Path == "/library/metadata/101"),
            request => Assert.Equal("1", request.Query.GetValueOrDefault("includeGuids")));
    }

    [Theory]
    [InlineData("201")]
    [InlineData("2011")]
    [InlineData("20111")]
    public async Task Seasons_and_episodes_resolve_to_their_show(string itemId)
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();

        MediaItemLabels? item = await harness.Media.GetLabelsAsync(itemId);

        Assert.NotNull(item);
        Assert.Equal("201", item.Id);
        Assert.Equal(MediaKind.Show, item.Kind);
        Assert.Equal("2", item.LibraryId);
        Assert.Equal("plex://show/5d9c086c46115600200aa2fe", item.Guid);
    }

    [Fact]
    public async Task Unknown_and_malformed_items_are_not_found()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();

        MediaItemLabels? unknown = await harness.Media.GetLabelsAsync("999");
        MediaItemLabels? malformed = await harness.Media.GetLabelsAsync("../identity");

        Assert.Null(unknown);
        Assert.Null(malformed);
        Assert.Equal(
            ["/library/metadata/999"],
            harness.Server.Requests.Where(request => request.Path.StartsWith("/library/", StringComparison.Ordinal)).Select(request => request.Path));
    }

    [Fact]
    public async Task Only_movies_and_shows_can_be_labelled()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();

        await Assert.ThrowsAsync<MediaServerException>(() => harness.Media.GetLabelsAsync("501"));
    }

    [Fact]
    public async Task Setting_labels_sends_the_full_set_and_locks_the_field()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        harness.Server.Item("201").SetLabels("Favorites");
        MediaItemLabels item = (await harness.Media.GetLabelsAsync("201"))!;

        await harness.Media.SetLabelsAsync(item, ["Favorites", "Ada & Ben", "favorites"]);

        FakePlexServer.RecordedRequest put = Assert.Single(harness.Server.Requests, request => request.Method == "PUT");
        Assert.Equal("/library/sections/2/all", put.Path);
        Assert.Equal(FakePlexServer.ServerToken, put.Token);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["type"] = "2",
                ["id"] = "201",
                ["label[0].tag.tag"] = "Favorites",
                ["label[1].tag.tag"] = "Ada & Ben",
                ["label.locked"] = "1",
            },
            put.Query);
        Assert.Equal(["Favorites", "Ada & Ben"], harness.Server.Item("201").Labels);
        Assert.True(harness.Server.Item("201").Locked);
    }

    [Fact]
    public async Task Setting_an_empty_set_removes_the_current_labels()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        harness.Server.Item("101").SetLabels("Ada", "Ben");
        MediaItemLabels item = (await harness.Media.GetLabelsAsync("101"))!;

        await harness.Media.SetLabelsAsync(item, []);

        FakePlexServer.RecordedRequest put = Assert.Single(harness.Server.Requests, request => request.Method == "PUT");
        Assert.Equal("Ada,Ben", put.Query["label[].tag.tag-"]);
        Assert.Equal("1", put.Query["label.locked"]);
        Assert.Empty(harness.Server.Item("101").Labels);
        Assert.True(harness.Server.Item("101").Locked);
    }
}
