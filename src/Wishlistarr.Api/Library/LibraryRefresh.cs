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

    public IDisposable? TryEnter() => this.gate.Wait(0) ? new Releaser(this.gate) : null;

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
    private static readonly TimeSpan CursorOverlap = TimeSpan.FromSeconds(5);

    private readonly Dictionary<(MediaKind Kind, int TmdbId), int> titleIds = [];
    private bool tmdbUnavailable;

    public async Task FullAsync(CancellationToken ct)
    {
        using IDisposable held = await gate.EnterAsync(ct);
        await this.RunFullAsync(await scanner.ScanAsync(null, ct), ct);
    }

    public async Task IncrementalAsync(CancellationToken ct)
    {
        using IDisposable held = await gate.EnterAsync(ct);
        LibraryRefreshState? state = await db.LibraryRefreshStates.AsNoTracking().SingleOrDefaultAsync(ct);
        if (state is not { LastFullRefreshAt: not null, UpdatedCursor: DateTimeOffset cursor })
        {
            await this.RunFullAsync(await scanner.ScanAsync(null, ct), ct);
            return;
        }

        LibraryScan scan = await scanner.ScanAsync(cursor - CursorOverlap, ct);
        if (state.ServerId != scan.ServerId)
        {
            await this.RunFullAsync(await scanner.ScanAsync(null, ct), ct);
            return;
        }

        DateTimeOffset? newest = await this.IndexAsync(scan, null, ct);
        await this.SyncLabelMembershipAsync(scan.ServerId, ct);
        await this.RemoveOrphanTitlesAsync(ct);
        LibraryRefreshState current = await db.LibraryRefreshStates.SingleAsync(ct);
        current.LastIncrementalRefreshAt = time.GetUtcNow();
        current.UpdatedCursor = Latest(current.UpdatedCursor, newest);
        await db.SaveChangesAsync(ct);
    }

    public async Task RetryUnresolvedAsync(CancellationToken ct)
    {
        using IDisposable held = await gate.EnterAsync(ct);
        string? serverId = await scanner.GetServerIdAsync(ct);
        if (serverId is null)
        {
            return;
        }

        // Failed lookups do not change Plex's timestamp, so the incremental cursor cannot decide which mappings to retry.
        int after = 0;
        while (true)
        {
            var unresolved = await db.LibraryItems.AsNoTracking()
                .Where(item => item.Id > after && item.ServerId == serverId && item.TitleId == null
                    && !item.ExternalIds.Any(id => id.Source == LibraryIdSource.Tmdb)
                    && item.ExternalIds.Any(id => id.Source == LibraryIdSource.Tvdb || id.Source == LibraryIdSource.Imdb))
                .OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.RatingKey })
                .Take(BatchSize)
                .ToListAsync(ct);
            if (unresolved.Count == 0)
            {
                return;
            }

            foreach (var item in unresolved)
            {
                after = item.Id;
                LibrarySnapshot? snapshot = await scanner.GetEntryAsync(item.RatingKey, ct);
                if (snapshot?.ServerId == serverId)
                {
                    await this.UpsertAsync(serverId, [snapshot.Entry], ct);
                }
            }
        }
    }

    public async Task<bool> TryItemAsync(string ratingKey, CancellationToken ct)
    {
        if (gate.TryEnter() is not IDisposable held)
        {
            return false;
        }

        using (held)
        {
            string? serverId = await db.LibraryRefreshStates.Select(state => state.ServerId).SingleOrDefaultAsync(ct);
            if (serverId is null)
            {
                return true;
            }

            LibrarySnapshot? snapshot = await scanner.GetEntryAsync(ratingKey, ct);
            if (snapshot is null)
            {
                await db.LibraryItems.Where(item => item.ServerId == serverId && item.RatingKey == ratingKey).ExecuteDeleteAsync(ct);
                await this.RemoveOrphanTitlesAsync(ct);
                return true;
            }

            if (snapshot.ServerId == serverId)
            {
                await this.UpsertAsync(snapshot.ServerId, [snapshot.Entry], ct);
                await this.RemoveOrphanTitlesAsync(ct);
            }

            return true;
        }
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
        bool hadItems = await db.LibraryItems.AnyAsync(item => item.ServerId == scan.ServerId, ct);

        bool confirmedEmpty = false;
        if (seen.Count == 0 && hadItems && scan.Report.Complete)
        {
            scan = await scanner.ScanAsync(null, ct);
            seen.Clear();
            newest = await this.IndexAsync(scan, seen, ct);
            confirmedEmpty = seen.Count == 0 && scan.Report.Complete;
        }

        bool complete = scan.Report.Complete && (seen.Count > 0 || !hadItems || confirmedEmpty);
        if (!complete)
        {
            LibraryRefreshState attempted = await db.LibraryRefreshStates.SingleOrDefaultAsync(ct) ?? db.LibraryRefreshStates.Add(new()).Entity;
            attempted.LastFullAttemptAt = time.GetUtcNow();
            attempted.LastFullAttemptComplete = false;
            await db.SaveChangesAsync(ct);
            this.LogIncomplete(seen.Count);
            return;
        }

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
        await this.SyncLabelMembershipAsync(scan.ServerId, ct);

        DateTimeOffset now = time.GetUtcNow();
        LibraryRefreshState state = await db.LibraryRefreshStates.SingleOrDefaultAsync(ct) ?? db.LibraryRefreshStates.Add(new()).Entity;
        state.ServerId = scan.ServerId;
        state.LastFullRefreshAt = now;
        state.LastFullAttemptAt = now;
        state.LastFullAttemptComplete = true;
        state.UpdatedCursor = newest;
        await db.SaveChangesAsync(ct);
        this.LogRefreshed(seen.Count);
    }

    private async Task SyncLabelMembershipAsync(string serverId, CancellationToken ct)
    {
        LibraryLabelScan labels;
        try
        {
            labels = await scanner.ScanLabelsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            this.LogLabelsFailed(ex);
            return;
        }

        if (labels.ServerId != serverId || !labels.Complete)
        {
            return;
        }

        Dictionary<string, int> itemIds = (await db.LibraryItems
            .Where(item => item.ServerId == serverId)
            .Select(item => new { item.Id, item.RatingKey })
            .ToListAsync(ct)).ToDictionary(item => item.RatingKey, item => item.Id, StringComparer.Ordinal);
        Dictionary<(int ItemId, string Label), string> desired = [];
        foreach ((string ratingKey, IReadOnlyList<string> tags) in labels.LabelsByItem)
        {
            if (!itemIds.TryGetValue(ratingKey, out int itemId))
            {
                continue;
            }

            foreach (string tag in tags.Select(tag => tag.Trim()).Where(tag => tag.Length is > 0 and <= LibraryItemLabel.MaxLength))
            {
                desired.TryAdd((itemId, tag.ToUpperInvariant()), tag);
            }
        }

        var existing = await db.LibraryItemLabels
            .Where(label => label.Item!.ServerId == serverId)
            .Select(label => new { label.Id, label.ItemId, label.Label })
            .ToListAsync(ct);
        foreach (int[] chunk in existing.Where(row => !desired.ContainsKey((row.ItemId, row.Label.ToUpperInvariant()))).Select(row => row.Id).Chunk(500))
        {
            await db.LibraryItemLabels.Where(label => chunk.Contains(label.Id)).ExecuteDeleteAsync(ct);
        }

        HashSet<(int, string)> present = [.. existing.Select(row => (row.ItemId, row.Label.ToUpperInvariant()))];
        db.LibraryItemLabels.AddRange(desired
            .Where(pair => !present.Contains(pair.Key))
            .Select(pair => new LibraryItemLabel { ItemId = pair.Key.ItemId, Label = pair.Value }));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
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
        List<(LibraryItem Item, int? TmdbId)> pending = [];

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
            item.PlexAddedAtUnix = entry.AddedAt?.ToUnixTimeSeconds();
            item.IndexedAt = now;
            if (entry.Labels is not null)
            {
                SyncLabels(item, entry.Labels);
            }

            pending.Add((item, await this.SyncIdsAsync(item, media, now, ct)));
        }

        await this.AssignTitlesAsync(pending, ct);
        db.ChangeTracker.Clear();
    }

    private async Task<int?> SyncIdsAsync(LibraryItem item, MediaItem media, DateTimeOffset now, CancellationToken ct)
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
        return tmdbValue is not null && int.TryParse(tmdbValue, NumberStyles.None, CultureInfo.InvariantCulture, out int tmdbId) ? tmdbId : null;
    }

    private async Task AssignTitlesAsync(IReadOnlyList<(LibraryItem Item, int? TmdbId)> pending, CancellationToken ct)
    {
        foreach (IGrouping<MediaKind, (LibraryItem Item, int? TmdbId)> group in pending.Where(entry => entry.TmdbId is not null && !this.titleIds.ContainsKey((entry.Item.Kind, entry.TmdbId!.Value)))
            .GroupBy(entry => entry.Item.Kind))
        {
            int[] wanted = [.. group.Select(entry => entry.TmdbId!.Value).Distinct()];
            foreach (var row in await db.LibraryTitles
                .Where(title => title.Kind == group.Key && wanted.Contains(title.TmdbId))
                .Select(title => new { title.Id, title.TmdbId })
                .ToListAsync(ct))
            {
                this.titleIds[(group.Key, row.TmdbId)] = row.Id;
            }
        }

        Dictionary<(MediaKind Kind, int TmdbId), LibraryTitle> created = [];
        foreach ((LibraryItem item, int? tmdbId) in pending)
        {
            if (tmdbId is not int id)
            {
                item.TitleId = null;
            }
            else if (this.titleIds.TryGetValue((item.Kind, id), out int known))
            {
                item.TitleId = known;
            }
            else
            {
                if (!created.TryGetValue((item.Kind, id), out LibraryTitle? title))
                {
                    created[(item.Kind, id)] = title = db.LibraryTitles.Add(new LibraryTitle { Kind = item.Kind, TmdbId = id }).Entity;
                }

                item.CanonicalTitle = title;
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (((MediaKind kind, int tmdbId), LibraryTitle title) in created)
        {
            this.titleIds[(kind, tmdbId)] = title.Id;
        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library refresh read {Count} items but Plex reported more or the index would be emptied; nothing was removed and the index is not marked refreshed.")]
    private partial void LogIncomplete(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading library labels failed; the stored labels were kept.")]
    private partial void LogLabelsFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Library refresh indexed {Count} items.")]
    private partial void LogRefreshed(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TMDB lookup failed for a {Kind} by {Source} id.")]
    private partial void LogResolveFailed(Exception exception, MediaKind kind, ExternalIdSource source);
}
