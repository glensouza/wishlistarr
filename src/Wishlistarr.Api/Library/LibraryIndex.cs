using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Library;

public sealed class LibraryIndex(WishlistarrDbContext db, ILibraryScanner scanner) : ILibraryIndex
{
    private Task<string?>? readyServer;

    public async Task<IReadOnlyList<MediaItem>?> FindAsync(MediaKind kind, ExternalId id, CancellationToken ct = default)
    {
        if (await this.GetReadyServerAsync(ct) is not string serverId)
        {
            return null;
        }

        List<LibraryItem> items;
        if (id.Source == ExternalIdSource.Tmdb)
        {
            if (!int.TryParse(id.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int tmdbId))
            {
                return [];
            }

            items = await db.LibraryTitles
                .Where(title => title.Kind == kind && title.TmdbId == tmdbId)
                .SelectMany(title => title.Items)
                .Where(item => item.ServerId == serverId)
                .AsNoTracking()
                .Include(item => item.ExternalIds)
                .OrderBy(item => item.Id)
                .ToListAsync(ct);
        }
        else
        {
            LibraryIdSource source = id.Source == ExternalIdSource.Tvdb ? LibraryIdSource.Tvdb : LibraryIdSource.Imdb;
            List<LibraryExternalId> matches = await db.LibraryExternalIds
                .Where(external => external.Source == source && external.Value == id.Value
                    && external.Item!.Kind == kind && external.Item.ServerId == serverId)
                .AsNoTrackingWithIdentityResolution()
                .Include(external => external.Item)
                .ThenInclude(item => item!.ExternalIds)
                .OrderBy(external => external.ItemId)
                .ToListAsync(ct);
            items = [.. matches.Select(external => external.Item!)];
        }

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
            state?.LastFullAttemptAt,
            state?.LastFullAttemptComplete,
            await db.LibraryItems.CountAsync(ct),
            await db.LibraryItems.CountAsync(item => item.TitleId == null, ct),
            await db.LibraryItemLabels.Select(label => label.Label).Distinct().CountAsync(ct));
    }

    private Task<string?> GetReadyServerAsync(CancellationToken ct) => this.readyServer ??= this.LoadReadyServerAsync(ct);

    private async Task<string?> LoadReadyServerAsync(CancellationToken ct)
    {
        LibraryRefreshState? state = await db.LibraryRefreshStates.AsNoTracking().SingleOrDefaultAsync(ct);
        return state is { LastFullRefreshAt: not null, ServerId: string serverId } && await scanner.GetServerIdAsync(ct) == serverId
            ? serverId
            : null;
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
    DateTimeOffset? LastFullAttemptAt,
    bool? LastFullAttemptComplete,
    int ItemCount,
    int UnresolvedCount,
    int LabelCount);
