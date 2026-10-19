using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Plex;

namespace Wishlistarr.Tests.Plex;

public sealed class PlexLibraryTests
{
    [Fact]
    public async Task Libraries_are_listed_with_type_and_agent()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();

        IReadOnlyList<MediaLibrary> libraries = await harness.Media.GetLibrariesAsync();

        Assert.Equal(
            [
                new MediaLibrary("1", "Movies", MediaLibraryType.Movie, "tv.plex.agents.movie"),
                new MediaLibrary("2", "TV Shows", MediaLibraryType.Show, "tv.plex.agents.series"),
                new MediaLibrary("3", "Old Movies", MediaLibraryType.Movie, "com.plexapp.agents.imdb"),
                new MediaLibrary("4", "Cartoons", MediaLibraryType.Show, "com.plexapp.agents.thetvdb"),
                new MediaLibrary("5", "Music", MediaLibraryType.Music, "tv.plex.agents.music"),
            ],
            libraries);
        Assert.All(
            harness.Server.Requests.Where(request => request.Path == "/library/sections"),
            request => Assert.Equal(FakePlexServer.ServerToken, request.Token));
    }

    [Fact]
    public async Task Connection_test_reports_each_state()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();

        MediaServerTestResult notLinked = await harness.Media.TestConnectionAsync();
        await harness.LinkAsync();
        MediaServerTestResult noServer = await harness.Media.TestConnectionAsync();
        await harness.Account.SelectServerAsync(FakePlexServer.MachineIdentifier);
        MediaServerTestResult connected = await harness.Media.TestConnectionAsync();
        harness.Server.ServerTokenOverride = "rotated";
        MediaServerTestResult rejected = await harness.Media.TestConnectionAsync();

        Assert.False(notLinked.Valid);
        Assert.Equal("Plex is not linked.", notLinked.Message);
        Assert.False(noServer.Valid);
        Assert.Equal("No Plex server is selected.", noServer.Message);
        Assert.True(connected.Valid);
        Assert.Equal("Living Room Server", connected.ServerName);
        Assert.Equal("1.41.3.9314-a0bfb8370", connected.Version);
        Assert.False(rejected.Valid);
    }

    [Fact]
    public async Task Home_and_shared_users_are_listed_once_each()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAsync();

        IReadOnlyList<MediaServerUser> users = await harness.Media.GetUsersAsync();

        Assert.Equal(
            [
                new MediaServerUser("5551001", "Parent Fixture", "parentfixture", true, false, true),
                new MediaServerUser("5551002", "Sam", null, true, true, false),
                new MediaServerUser("5551003", "Alex", "alexplays", true, false, false),
                new MediaServerUser("7770002", "Grandma", "grandma", false, false, false),
                new MediaServerUser("7770004", "cousin_kim", "cousin_kim", false, true, false),
            ],
            users);
    }

    [Theory]
    [InlineData(MediaKind.Movie, ExternalIdSource.Tmdb, "603", new[] { "101", "302" })]
    [InlineData(MediaKind.Movie, ExternalIdSource.Imdb, "tt0133093", new[] { "101" })]
    [InlineData(MediaKind.Movie, ExternalIdSource.Imdb, "TT0111161", new[] { "301" })]
    [InlineData(MediaKind.Movie, ExternalIdSource.Tmdb, "129", new[] { "102" })]
    [InlineData(MediaKind.Show, ExternalIdSource.Tmdb, "129", new[] { "202" })]
    [InlineData(MediaKind.Show, ExternalIdSource.Tvdb, "353546", new[] { "201" })]
    [InlineData(MediaKind.Show, ExternalIdSource.Imdb, "tt7678620", new[] { "201" })]
    [InlineData(MediaKind.Show, ExternalIdSource.Tvdb, "74852", new[] { "401" })]
    [InlineData(MediaKind.Show, ExternalIdSource.Tvdb, "169", new string[0])]
    [InlineData(MediaKind.Movie, ExternalIdSource.Tmdb, "999999", new string[0])]
    public async Task Lookup_matches_new_and_legacy_agent_guids(MediaKind kind, ExternalIdSource source, string value, string[] expected)
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();
        Assert.True(ExternalId.TryCreate(source, value, out ExternalId? id));

        IReadOnlyList<MediaItem> items = await harness.Media.FindByExternalIdAsync(kind, id);

        Assert.Equal(expected, items.Select(item => item.Id).Order(StringComparer.Ordinal));
        Assert.All(items, item => Assert.Equal(kind, item.Kind));
    }

    [Fact]
    public async Task Lookup_returns_titles_libraries_and_every_known_id()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync();
        await harness.LinkAndSelectAsync();

        IReadOnlyList<MediaItem> items = await harness.Media.FindByExternalIdAsync(MediaKind.Movie, new ExternalId(ExternalIdSource.Tmdb, "603"));

        MediaItem modern = items.Single(item => item.Id == "101");
        MediaItem legacy = items.Single(item => item.Id == "302");
        Assert.Equal("The Matrix", modern.Title);
        Assert.Equal(1999, modern.Year);
        Assert.Equal("1", modern.LibraryId);
        Assert.Equal("plex://movie/5d7768ba96b655001fdc0408", modern.Guid);
        Assert.Equal(
            [new(ExternalIdSource.Imdb, "tt0133093"), new(ExternalIdSource.Tmdb, "603"), new(ExternalIdSource.Tvdb, "169")],
            modern.ExternalIds);
        Assert.Equal("3", legacy.LibraryId);
        Assert.Equal([new ExternalId(ExternalIdSource.Tmdb, "603")], legacy.ExternalIds);
    }

    [Fact]
    public async Task Lookup_pages_through_every_library_of_the_kind_and_caches_the_result()
    {
        await using PlexHarness harness = await PlexHarness.CreateAsync(options => options.PageSize = 1);
        await harness.LinkAndSelectAsync();

        IReadOnlyList<MediaItem> first = await harness.Media.FindByExternalIdAsync(MediaKind.Movie, new ExternalId(ExternalIdSource.Imdb, "tt0245429"));
        IReadOnlyList<MediaItem> second = await harness.Media.FindByExternalIdAsync(MediaKind.Movie, new ExternalId(ExternalIdSource.Imdb, "tt0111161"));

        Assert.Equal("102", Assert.Single(first).Id);
        Assert.Equal("301", Assert.Single(second).Id);
        Assert.Equal(3, harness.Server.Count("/library/sections/1/all"));
        Assert.Equal(2, harness.Server.Count("/library/sections/3/all"));
        Assert.Equal(0, harness.Server.Count("/library/sections/2/all"));
        Assert.Equal(
            ["0", "1", "2"],
            harness.Server.Requests.Where(request => request.Path == "/library/sections/1/all").Select(request => request.Query["X-Plex-Container-Start"]));
        Assert.All(
            harness.Server.Requests.Where(request => request.Path.EndsWith("/all", StringComparison.Ordinal)),
            request =>
            {
                Assert.Equal("1", request.Query["includeGuids"]);
                Assert.Equal("1", request.Query["type"]);
            });
    }

    [Theory]
    [InlineData("tmdb://603", ExternalIdSource.Tmdb, "603")]
    [InlineData("tvdb://81189", ExternalIdSource.Tvdb, "81189")]
    [InlineData("imdb://tt0133093", ExternalIdSource.Imdb, "tt0133093")]
    [InlineData("com.plexapp.agents.imdb://tt0133093?lang=en", ExternalIdSource.Imdb, "tt0133093")]
    [InlineData("com.plexapp.agents.themoviedb://603?lang=en", ExternalIdSource.Tmdb, "603")]
    [InlineData("com.plexapp.agents.thetvdb://81189?lang=en", ExternalIdSource.Tvdb, "81189")]
    [InlineData("com.plexapp.agents.thetvdb://81189/1/2?lang=en", ExternalIdSource.Tvdb, "81189")]
    [InlineData("com.plexapp.agents.themoviedb://00603?lang=en", ExternalIdSource.Tmdb, "603")]
    public void Guids_from_both_agent_generations_are_parsed(string guid, ExternalIdSource source, string value)
    {
        Assert.True(PlexGuids.TryParse(guid, out ExternalId? id));
        Assert.Equal(new ExternalId(source, value), id);
    }

    [Theory]
    [InlineData("plex://movie/5d7768ba96b655001fdc0408")]
    [InlineData("local://103")]
    [InlineData("com.plexapp.agents.none://402?lang=xn")]
    [InlineData("com.plexapp.agents.hama://anidb-123?lang=en")]
    [InlineData("imdb://0133093")]
    [InlineData("tmdb://abc")]
    [InlineData("tmdb://")]
    [InlineData("tmdb://0")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_guids_are_ignored(string? guid) =>
        Assert.False(PlexGuids.TryParse(guid, out _));
}
