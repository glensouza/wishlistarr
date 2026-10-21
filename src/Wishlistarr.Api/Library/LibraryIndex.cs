using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Library;

public sealed class LibraryIndex(WishlistarrDbContext db) : ILibraryIndex
{
    public async Task<IReadOnlyList<MediaItem>?> FindAsync(MediaKind kind, ExternalId id, CancellationToken ct = default)
    {
        if (!await db.LibraryRefreshStates.AsNoTracking().AnyAsync(state => state.LastFullRefreshAt != null, ct))
        {
            return null;
        }

        IQueryable<LibraryItem> matches;
        if (id.Source == ExternalIdSource.Tmdb)
        {
            if (!int.TryParse(id.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int tmdbId))
            {
                return [];
            }

            matches = db.LibraryItems.Where(item => item.Kind == kind && item.CanonicalTitle!.TmdbId == tmdbId);
        }
        else
        {
            LibraryIdSource source = id.Source == ExternalIdSource.Tvdb ? LibraryIdSource.Tvdb : LibraryIdSource.Imdb;
            matches = db.LibraryItems.Where(item => item.Kind == kind
                && item.ExternalIds.Any(external => external.Source == source && external.Value == id.Value));
        }

        List<LibraryItem> items = await matches
            .AsNoTracking()
            .Include(item => item.ExternalIds)
            .OrderBy(item => item.Id)
            .ToListAsync(ct);
        return [.. items.Select(ToMediaItem)];
    }

    public async Task<LibraryStatus> GetStatusAsync(CancellationToken ct = default)
    {
        LibraryRefreshState? state = await db.LibraryRefreshStates.AsNoTracking().SingleOrDefaultAsync(ct);
        return new LibraryStatus(
            state?.LastFullRefreshAt is not null,
            state?.ServerId,
            state?.LastFullRefreshAt,
            state?.LastIncrementalRefreshAt,
            await db.LibraryItems.CountAsync(ct),
            await db.LibraryItems.CountAsync(item => item.TitleId == null, ct),
            await db.LibraryItemLabels.Select(label => label.Label).Distinct().CountAsync(ct));
    }

    private static MediaItem ToMediaItem(LibraryItem item) => new(
        item.RatingKey,
        item.Guid,
        item.Title,
        item.Year,
        item.Kind,
        item.LibraryId,
        [.. item.ExternalIds.Select(ToExternalId).OfType<ExternalId>().OrderBy(id => id.Source)]);

    private static ExternalId? ToExternalId(LibraryExternalId id) => id.Source switch
    {
        LibraryIdSource.Tmdb => new ExternalId(ExternalIdSource.Tmdb, id.Value),
        LibraryIdSource.Tvdb => new ExternalId(ExternalIdSource.Tvdb, id.Value),
        LibraryIdSource.Imdb => new ExternalId(ExternalIdSource.Imdb, id.Value),
        _ => null,
    };
}

public sealed record LibraryStatus(
    bool Ready,
    string? ServerId,
    DateTimeOffset? LastFullRefreshAt,
    DateTimeOffset? LastIncrementalRefreshAt,
    int ItemCount,
    int UnresolvedCount,
    int LabelCount);
