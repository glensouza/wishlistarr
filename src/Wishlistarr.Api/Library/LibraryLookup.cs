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

    private static List<MediaItem> Matching(IReadOnlyList<MediaItem> items, MediaKind kind) =>
        [.. items.Where(item => item.Kind == kind).DistinctBy(item => item.Id)];
}
