using Wishlistarr.Api.Library;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Tests.Requests;

public sealed class LibraryLookupTests
{
    private static readonly MediaItem Live = new("101", "plex://movie/a", "Live", 1999, MediaKind.Movie, "1", []);
    private static readonly MediaItem Indexed = new("102", "plex://movie/b", "Indexed", 2001, MediaKind.Movie, "1", []);

    [Fact]
    public async Task Without_an_index_plex_is_asked_by_tmdb_id()
    {
        LiveServer server = new([Live]);
        LibraryLookup lookup = new(server, []);

        IReadOnlyList<MediaItem> found = await lookup.FindAsync(new TitleKey(MediaKind.Movie, 603));

        Assert.Equal([Live], found);
        Assert.Equal((MediaKind.Movie, new ExternalId(ExternalIdSource.Tmdb, "603")), Assert.Single(server.Lookups));
    }

    [Fact]
    public async Task A_ready_index_answers_without_plex()
    {
        LiveServer server = new([Live]);
        FakeLibraryIndex index = new();
        index.Add(MediaKind.Movie, 603, Indexed);
        LibraryLookup lookup = new(server, [index]);

        Assert.Equal([Indexed], await lookup.FindAsync(new TitleKey(MediaKind.Movie, 603)));
        Assert.Empty(await lookup.FindAsync(new TitleKey(MediaKind.Movie, 604)));
        Assert.Empty(server.Lookups);
    }

    [Fact]
    public async Task An_index_that_cannot_answer_falls_back_to_plex()
    {
        LiveServer server = new([Live]);
        LibraryLookup lookup = new(server, [new FakeLibraryIndex { Ready = false }]);

        Assert.Equal([Live], await lookup.FindAsync(new TitleKey(MediaKind.Movie, 603)));
        Assert.Single(server.Lookups);
    }

    [Fact]
    public async Task Matches_of_another_kind_and_duplicates_are_dropped()
    {
        MediaItem show = new("201", "plex://show/c", "Show", 2018, MediaKind.Show, "2", []);
        LibraryLookup lookup = new(new LiveServer([Live, show, Live]), []);

        Assert.Equal([Live], await lookup.FindAsync(new TitleKey(MediaKind.Movie, 603)));
    }

    private sealed class LiveServer(IReadOnlyList<MediaItem> items) : IMediaServer
    {
        public List<(MediaKind Kind, ExternalId Id)> Lookups { get; } = [];

        public Task<IReadOnlyList<MediaItem>> FindByExternalIdAsync(MediaKind kind, ExternalId id, CancellationToken ct = default)
        {
            this.Lookups.Add((kind, id));
            return Task.FromResult(items);
        }

        public Task<MediaServerTestResult> TestConnectionAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<MediaServerUser>> GetUsersAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MediaItemLabels?> GetLabelsAsync(string itemId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetLabelsAsync(MediaItemLabels item, IReadOnlyList<string> labels, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
