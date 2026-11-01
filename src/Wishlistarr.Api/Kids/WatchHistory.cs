using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tautulli;

namespace Wishlistarr.Api.Kids;

public sealed record TitleWatchState(MediaKind Kind, int TmdbId, bool Finished, int PercentComplete, long LastPlayed);

public sealed record ProfileWatchState(bool Available, IReadOnlyList<TitleWatchState> Titles);

public sealed class WatchHistory(WishlistarrDbContext db, ILibraryScanner scanner, IWatchIdentityResolver identities, TautulliHistory tautulli)
{
    public async Task<ProfileWatchState?> ReadAsync(int profileId, CancellationToken ct = default)
    {
        KidProfile? profile = await db.KidProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == profileId && p.ArchivedAt == null, ct);
        if (profile is null) return null;
        if (string.IsNullOrEmpty(profile.PlexUserId)) return new(false, []);
        string? serverId;
        try
        {
            serverId = await scanner.GetServerIdAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is MediaServerException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, []);
        }
        LibraryRefreshState? index = await db.LibraryRefreshStates.AsNoTracking().SingleOrDefaultAsync(ct);
        if (serverId is null || index?.ServerId != serverId || index.LastFullRefreshAt is null) return new(false, []);
        IReadOnlyList<WatchEvent>? events = await tautulli.ReadAsync(profile.PlexUserId, serverId, ct);
        if (events is null) return new(false, []);
        string? currentUser = await db.KidProfiles.AsNoTracking()
            .Where(p => p.Id == profileId && p.ArchivedAt == null)
            .Select(p => p.PlexUserId).SingleOrDefaultAsync(ct);
        try
        {
            if (currentUser != profile.PlexUserId || await scanner.GetServerIdAsync(ct) != serverId) return new(false, []);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is MediaServerException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, []);
        }
        List<string> keys = [.. events.Select(e => e.TitleKey).Distinct()];
        List<LibraryItem> items = await db.LibraryItems.AsNoTracking().Include(i => i.CanonicalTitle)
            .Where(i => i.ServerId == serverId && keys.Contains(i.RatingKey) && i.TitleId != null).ToListAsync(ct);
        Dictionary<string, LibraryItem> titles = items.Where(i => i.CanonicalTitle is not null).ToDictionary(i => i.RatingKey);
        WatchEvent[] candidates = [.. events.Where(e => titles.TryGetValue(e.TitleKey, out LibraryItem? item) && item.Kind == (e.Episode ? MediaKind.Show : MediaKind.Movie))
            .DistinctBy(e => (e.RatingKey, e.TitleKey, e.Guid, e.Episode)).Take(32)];
        ConcurrentDictionary<(string, string, string, bool), bool> verified = new();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = deadline.Token }, async (entry, token) =>
            {
                LibraryItem title = titles[entry.TitleKey];
                bool matches;
                try
                {
                    matches = await identities.MatchesAsync(serverId, entry.RatingKey, entry.Guid, entry.TitleKey, title.Guid, entry.Episode, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is MediaServerException or HttpRequestException or TaskCanceledException or JsonException)
                {
                    matches = false;
                }

                verified[(entry.RatingKey, entry.TitleKey, entry.Guid, entry.Episode)] = matches;
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, []);
        }

        currentUser = await db.KidProfiles.AsNoTracking()
            .Where(p => p.Id == profileId && p.ArchivedAt == null)
            .Select(p => p.PlexUserId).SingleOrDefaultAsync(ct);
        try
        {
            if (currentUser != profile.PlexUserId || await scanner.GetServerIdAsync(ct) != serverId) return new(false, []);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is MediaServerException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, []);
        }

        List<TitleWatchState> result = [.. events.Where(e => verified.GetValueOrDefault((e.RatingKey, e.TitleKey, e.Guid, e.Episode)))
            .GroupBy(e => (titles[e.TitleKey].CanonicalTitle!.Kind, titles[e.TitleKey].CanonicalTitle!.TmdbId))
            .Select(g => new TitleWatchState(g.Key.Kind, g.Key.TmdbId, g.Any(e => e.Finished), g.OrderByDescending(e => e.Date).First().PercentComplete, g.Max(e => e.Date)))
            .OrderByDescending(e => e.LastPlayed)];
        return new(true, result);
    }
}
