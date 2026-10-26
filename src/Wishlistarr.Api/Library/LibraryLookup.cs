using System.Globalization;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Api.Library;

public sealed class LibraryLookup(IMediaServer server, IEnumerable<ILibraryIndex> indexes)
{
    public async Task<IReadOnlyList<MediaItem>> FindAsync(TitleKey title, CancellationToken ct = default)
    {
        ExternalId id = new(ExternalIdSource.Tmdb, title.TmdbId.ToString(CultureInfo.InvariantCulture));
        foreach (ILibraryIndex index in indexes)
        {
            if (await index.FindAsync(title.Kind, id, ct) is IReadOnlyList<MediaItem> indexed)
            {
                return Matching(indexed, title.Kind);
            }
        }

        return Matching(await server.FindByExternalIdAsync(title.Kind, id, ct), title.Kind);
    }

    // Only the index answers, so a page of titles never turns into one Plex call per title.
    public async Task<IndexedGuids?> FindIndexedGuidsAsync(IReadOnlyCollection<TitleKey> titles, CancellationToken ct = default)
    {
        foreach (ILibraryIndex index in indexes)
        {
            if (await index.FindGuidsAsync(titles, ct) is IndexedGuids indexed)
            {
                return indexed;
            }
        }

        return null;
    }

    private static List<MediaItem> Matching(IReadOnlyList<MediaItem> items, MediaKind kind) =>
        [.. items.Where(item => item.Kind == kind).DistinctBy(item => item.Id)];
}
