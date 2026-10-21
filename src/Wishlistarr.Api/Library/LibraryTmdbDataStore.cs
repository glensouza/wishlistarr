using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Api.Tmdb;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Library;

public sealed class LibraryTmdbDataStore(WishlistarrDbContext db, ITmdbClient tmdb, TimeProvider time) : ITmdbDataStore
{
    private const int ChunkSize = 200;

    public Type EntityType => typeof(LibraryExternalId);

    public async Task RefreshAsync(DateTimeOffset staleBefore, CancellationToken ct)
    {
        List<int> stale = await this.FindIdsAsync(fetchedAt => fetchedAt is null || fetchedAt < staleBefore, ct);
        foreach (int[] chunk in stale.Chunk(ChunkSize))
        {
            List<LibraryExternalId> rows = await db.LibraryExternalIds
                .Include(row => row.Item)
                .ThenInclude(item => item!.ExternalIds)
                .Where(row => chunk.Contains(row.Id))
                .ToListAsync(ct);
            Dictionary<(MediaKind Kind, int TmdbId), LibraryTitle> created = [];
            foreach (LibraryExternalId row in rows)
            {
                if (await this.ResolveAsync(row, ct) is not int found)
                {
                    continue;
                }

                if (found.ToString(CultureInfo.InvariantCulture) == row.Value)
                {
                    row.FetchedAt = time.GetUtcNow();
                }
                else if (row.Item is LibraryItem item)
                {
                    row.Value = found.ToString(CultureInfo.InvariantCulture);
                    row.FetchedAt = time.GetUtcNow();
                    if (!created.TryGetValue((item.Kind, found), out LibraryTitle? title))
                    {
                        title = await db.LibraryTitles.SingleOrDefaultAsync(existing => existing.Kind == item.Kind && existing.TmdbId == found, ct)
                            ?? db.LibraryTitles.Add(new LibraryTitle { Kind = item.Kind, TmdbId = found }).Entity;
                        created[(item.Kind, found)] = title;
                    }

                    item.CanonicalTitle = title;
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        await this.DetachOrphanedTitlesAsync(ct);
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, CancellationToken ct)
    {
        List<int> expired = await this.FindIdsAsync(fetchedAt => fetchedAt is null || fetchedAt < expiredBefore, ct);
        foreach (int[] chunk in expired.Chunk(ChunkSize))
        {
            await db.LibraryExternalIds.Where(row => chunk.Contains(row.Id)).ExecuteDeleteAsync(ct);
        }

        await this.DetachOrphanedTitlesAsync(ct);
        return expired.Count;
    }

    public async Task<int> PurgeAllAsync(CancellationToken ct)
    {
        int purged = await db.LibraryExternalIds.Where(row => row.Origin == LibraryIdOrigin.TmdbLookup).ExecuteDeleteAsync(ct);
        await this.DetachOrphanedTitlesAsync(ct);
        return purged;
    }

    private async Task<int?> ResolveAsync(LibraryExternalId row, CancellationToken ct)
    {
        LibraryItem? item = row.Item;
        if (item is null)
        {
            return null;
        }

        TmdbMediaType type = item.Kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv;
        foreach (LibraryExternalId legacy in item.ExternalIds
            .Where(id => id.Origin == LibraryIdOrigin.Plex && id.Source is LibraryIdSource.Tvdb or LibraryIdSource.Imdb)
            .OrderBy(id => id.Source))
        {
            ExternalIdSource source = legacy.Source == LibraryIdSource.Tvdb ? ExternalIdSource.Tvdb : ExternalIdSource.Imdb;
            if (await tmdb.FindIdAsync(type, source, legacy.Value, ct) is int found)
            {
                return found;
            }
        }

        return null;
    }

    private async Task<List<int>> FindIdsAsync(Func<DateTimeOffset?, bool> match, CancellationToken ct)
    {
        var lookups = await db.LibraryExternalIds
            .Where(row => row.Origin == LibraryIdOrigin.TmdbLookup)
            .Select(row => new { row.Id, row.FetchedAt })
            .ToListAsync(ct);
        return [.. lookups.Where(row => match(row.FetchedAt)).Select(row => row.Id)];
    }

    private async Task DetachOrphanedTitlesAsync(CancellationToken ct)
    {
        await db.LibraryItems
            .Where(item => item.TitleId != null && !item.ExternalIds.Any(id => id.Source == LibraryIdSource.Tmdb))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.TitleId, (int?)null), ct);
        await db.LibraryTitles.Where(title => !title.Items.Any()).ExecuteDeleteAsync(ct);
    }
}
