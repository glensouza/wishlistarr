using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Library;

public sealed class LibraryRefreshGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        return new Releaser(this.gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

public sealed partial class LibraryRefresh(
    WishlistarrDbContext db,
    ILibraryScanner scanner,
    ITmdbClient tmdb,
    LibraryRefreshGate gate,
    TimeProvider time,
    ILogger<LibraryRefresh> logger)
{
    private const int BatchSize = 200;
    private static readonly TimeSpan CursorOverlap = TimeSpan.FromMinutes(5);

    private readonly Dictionary<(MediaKind Kind, int TmdbId), LibraryTitle> titles = [];
    private bool tmdbUnavailable;

    public async Task FullAsync(CancellationToken ct)
    {
        using IDisposable held = await gate.EnterAsync(ct);
        await this.RunFullAsync(await scanner.ScanAsync(null, ct), ct);
    }

    public async Task IncrementalAsync(CancellationToken ct)
    {
        using IDisposable held = await gate.EnterAsync(ct);
        LibraryRefreshState? state = await db.LibraryRefreshStates.SingleOrDefaultAsync(ct);
        if (state?.LastFullRefreshAt is not DateTimeOffset lastFull)
        {
            await this.RunFullAsync(await scanner.ScanAsync(null, ct), ct);
            return;
        }

        LibraryScan scan = await scanner.ScanAsync(state.UpdatedCursor ?? lastFull - CursorOverlap, ct);
        if (state.ServerId != scan.ServerId)
        {
            await this.RunFullAsync(await scanner.ScanAsync(null, ct), ct);
            return;
        }

        DateTimeOffset? newest = await this.IndexAsync(scan, null, ct);
        await this.RemoveOrphanTitlesAsync(ct);
        state.LastIncrementalRefreshAt = time.GetUtcNow();
        state.UpdatedCursor = Latest(state.UpdatedCursor, newest);
        await db.SaveChangesAsync(ct);
    }

    public async Task ItemAsync(string ratingKey, CancellationToken ct)
    {
        using IDisposable held = await gate.EnterAsync(ct);
        string? serverId = await db.LibraryRefreshStates.Select(state => state.ServerId).SingleOrDefaultAsync(ct);
        if (serverId is null)
        {
            return;
        }

        LibrarySnapshot? snapshot = await scanner.GetEntryAsync(ratingKey, ct);
        if (snapshot is null)
        {
            await db.LibraryItems.Where(item => item.ServerId == serverId && item.RatingKey == ratingKey).ExecuteDeleteAsync(ct);
            await this.RemoveOrphanTitlesAsync(ct);
            return;
        }

        if (snapshot.ServerId != serverId)
        {
            return;
        }

        await this.UpsertAsync(snapshot.ServerId, [snapshot.Entry], ct);
        await this.RemoveOrphanTitlesAsync(ct);
    }

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;

    private static bool IsLegacyKey(LibraryIdSource source) => source is LibraryIdSource.Tvdb or LibraryIdSource.Imdb;

    private static LibraryIdSource ToSource(ExternalIdSource source) => source switch
    {
        ExternalIdSource.Tmdb => LibraryIdSource.Tmdb,
        ExternalIdSource.Tvdb => LibraryIdSource.Tvdb,
        _ => LibraryIdSource.Imdb,
    };

    private static void SyncLabels(LibraryItem item, IReadOnlyList<string> labels)
    {
        List<string> wanted = [.. labels
            .Select(label => label.Trim())
            .Where(label => label.Length is > 0 and <= LibraryItemLabel.MaxLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        foreach (LibraryItemLabel stale in item.Labels.Where(label => !wanted.Contains(label.Label, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            item.Labels.Remove(stale);
        }

        foreach (string label in wanted.Where(label => !item.Labels.Any(current => string.Equals(current.Label, label, StringComparison.OrdinalIgnoreCase))))
        {
            item.Labels.Add(new LibraryItemLabel { Label = label });
        }
    }

    private Task RemoveOrphanTitlesAsync(CancellationToken ct) =>
        db.LibraryTitles.Where(title => !title.Items.Any()).ExecuteDeleteAsync(ct);

    private async Task RunFullAsync(LibraryScan scan, CancellationToken ct)
    {
        HashSet<string> seen = [];
        DateTimeOffset? newest = await this.IndexAsync(scan, seen, ct);

        await db.LibraryItems.Where(item => item.ServerId != scan.ServerId).ExecuteDeleteAsync(ct);
        var indexed = await db.LibraryItems
            .Where(item => item.ServerId == scan.ServerId)
            .Select(item => new { item.Id, item.RatingKey })
            .ToListAsync(ct);
        foreach (int[] chunk in indexed.Where(item => !seen.Contains(item.RatingKey)).Select(item => item.Id).Chunk(500))
        {
            await db.LibraryItems.Where(item => chunk.Contains(item.Id)).ExecuteDeleteAsync(ct);
        }

        await this.RemoveOrphanTitlesAsync(ct);

        LibraryRefreshState state = await db.LibraryRefreshStates.SingleOrDefaultAsync(ct) ?? db.LibraryRefreshStates.Add(new()).Entity;
        state.ServerId = scan.ServerId;
        state.LastFullRefreshAt = time.GetUtcNow();
        state.UpdatedCursor = newest;
        await db.SaveChangesAsync(ct);
        this.LogRefreshed(seen.Count);
    }

    private async Task<DateTimeOffset?> IndexAsync(LibraryScan scan, HashSet<string>? seen, CancellationToken ct)
    {
        DateTimeOffset? newest = null;
        List<LibraryEntry> batch = [];
        await foreach (LibraryEntry entry in scan.Entries.WithCancellation(ct))
        {
            seen?.Add(entry.Item.Id);
            newest = Latest(newest, entry.UpdatedAt);
            batch.Add(entry);
            if (batch.Count >= BatchSize)
            {
                await this.UpsertAsync(scan.ServerId, batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await this.UpsertAsync(scan.ServerId, batch, ct);
        }

        return newest;
    }

    private async Task UpsertAsync(string serverId, IReadOnlyList<LibraryEntry> entries, CancellationToken ct)
    {
        string[] keys = [.. entries.Select(entry => entry.Item.Id).Distinct(StringComparer.Ordinal)];
        Dictionary<string, LibraryItem> existing = await db.LibraryItems
            .Include(item => item.ExternalIds)
            .Include(item => item.Labels)
            .Where(item => item.ServerId == serverId && keys.Contains(item.RatingKey))
            .ToDictionaryAsync(item => item.RatingKey, StringComparer.Ordinal, ct);
        DateTimeOffset now = time.GetUtcNow();

        foreach (LibraryEntry entry in entries.DistinctBy(entry => entry.Item.Id))
        {
            MediaItem media = entry.Item;
            if (!existing.TryGetValue(media.Id, out LibraryItem? item))
            {
                item = new LibraryItem { ServerId = serverId, RatingKey = media.Id, Guid = media.Guid, LibraryId = media.LibraryId, Title = media.Title };
                db.LibraryItems.Add(item);
            }

            item.Guid = Truncate(media.Guid, 256);
            item.Kind = media.Kind;
            item.LibraryId = media.LibraryId;
            item.Title = Truncate(media.Title, 500);
            item.Year = media.Year;
            item.PlexUpdatedAt = entry.UpdatedAt;
            item.IndexedAt = now;
            SyncLabels(item, entry.Labels);
            await this.SyncIdsAsync(item, media, now, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SyncIdsAsync(LibraryItem item, MediaItem media, DateTimeOffset now, CancellationToken ct)
    {
        List<(LibraryIdSource Source, string Value)> wanted =
        [
            (LibraryIdSource.PlexRatingKey, media.Id),
            .. media.ExternalIds.Select(id => (ToSource(id.Source), id.Value)),
        ];
        if (media.Guid.Length > 0)
        {
            wanted.Add((LibraryIdSource.PlexGuid, Truncate(media.Guid, 256)));
        }

        bool plexTmdb = wanted.Any(id => id.Source == LibraryIdSource.Tmdb);
        bool legacyIdsChanged = !item.ExternalIds
            .Where(id => id.Origin == LibraryIdOrigin.Plex && IsLegacyKey(id.Source))
            .Select(id => (id.Source, id.Value))
            .Order()
            .SequenceEqual(wanted.Where(id => IsLegacyKey(id.Source)).Order());

        LibraryExternalId? lookup = item.ExternalIds.FirstOrDefault(id => id.Origin == LibraryIdOrigin.TmdbLookup);
        if (lookup is not null && (plexTmdb || legacyIdsChanged))
        {
            item.ExternalIds.Remove(lookup);
            lookup = null;
        }

        foreach (LibraryExternalId stale in item.ExternalIds
            .Where(id => id.Origin == LibraryIdOrigin.Plex && !wanted.Contains((id.Source, id.Value)))
            .ToList())
        {
            item.ExternalIds.Remove(stale);
        }

        foreach ((LibraryIdSource source, string value) in wanted
            .Where(id => !item.ExternalIds.Any(current => current.Source == id.Source && current.Value == id.Value))
            .Distinct())
        {
            item.ExternalIds.Add(new LibraryExternalId { Source = source, Value = value, Origin = LibraryIdOrigin.Plex });
        }

        if (!plexTmdb && lookup is null && await this.ResolveTmdbIdAsync(media, ct) is string resolved)
        {
            item.ExternalIds.Add(new LibraryExternalId
            {
                Source = LibraryIdSource.Tmdb,
                Value = resolved,
                Origin = LibraryIdOrigin.TmdbLookup,
                FetchedAt = now,
            });
        }

        string? tmdbValue = item.ExternalIds.FirstOrDefault(id => id.Source == LibraryIdSource.Tmdb)?.Value;
        if (tmdbValue is not null && int.TryParse(tmdbValue, NumberStyles.None, CultureInfo.InvariantCulture, out int tmdbId))
        {
            item.CanonicalTitle = await this.FindTitleAsync(item, tmdbId, ct);
        }
        else
        {
            item.CanonicalTitle = null;
            item.TitleId = null;
        }
    }

    private async Task<LibraryTitle> FindTitleAsync(LibraryItem item, int tmdbId, CancellationToken ct)
    {
        if (this.titles.TryGetValue((item.Kind, tmdbId), out LibraryTitle? known))
        {
            return known;
        }

        LibraryTitle title = await db.LibraryTitles.SingleOrDefaultAsync(row => row.Kind == item.Kind && row.TmdbId == tmdbId, ct)
            ?? db.LibraryTitles.Add(new LibraryTitle { Kind = item.Kind, TmdbId = tmdbId, Title = item.Title, Year = item.Year }).Entity;
        this.titles[(item.Kind, tmdbId)] = title;
        return title;
    }

    private async Task<string?> ResolveTmdbIdAsync(MediaItem media, CancellationToken ct)
    {
        if (this.tmdbUnavailable)
        {
            return null;
        }

        TmdbMediaType type = media.Kind == MediaKind.Movie ? TmdbMediaType.Movie : TmdbMediaType.Tv;
        foreach (ExternalId id in media.ExternalIds
            .Where(id => id.Source is ExternalIdSource.Tvdb or ExternalIdSource.Imdb)
            .OrderBy(id => id.Source == ExternalIdSource.Tvdb ? 0 : 1))
        {
            try
            {
                if (await tmdb.FindIdAsync(type, id.Source, id.Value, ct) is int found)
                {
                    return found.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (TmdbNotConfiguredException)
            {
                this.tmdbUnavailable = true;
                return null;
            }
            catch (Exception ex) when (ex is TmdbException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                this.LogResolveFailed(ex, media.Kind, id.Source);
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Library refresh indexed {Count} items.")]
    private partial void LogRefreshed(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TMDB lookup failed for a {Kind} by {Source} id.")]
    private partial void LogResolveFailed(Exception exception, MediaKind kind, ExternalIdSource source);
}
