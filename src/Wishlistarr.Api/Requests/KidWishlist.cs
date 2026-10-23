using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public enum KidWishState
{
    Waiting,
    OnTheWay,
    Ready,
    Denied,
}

public sealed record KidWishlistResponse(int? Remaining, DateTimeOffset? ResetsAt, IReadOnlyList<KidWishItem> Items);

public sealed record KidWishItem(int Id, MediaKind Kind, int TmdbId, string? Title, KidWishState State, string? Note, KidPlexLinks? Plex);

public sealed record KidPlexLinks(string WebUrl, string AppUrl);

public sealed partial class KidWishlist(
    WishlistarrDbContext db,
    KidRequestFlow flow,
    IEnumerable<ILibraryIndex> indexes,
    ILibraryScanner scanner,
    ILogger<KidWishlist> logger)
{
    public async Task<KidWishlistResponse?> ListAsync(int profileId, CancellationToken ct = default)
    {
        KidProfile? profile = await db.KidProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == profileId && row.ArchivedAt == null, ct);
        if (profile is null)
        {
            return null;
        }

        // SQLite cannot order by the stored DateTimeOffset, so the newest-first order is applied after the rows are loaded.
        List<MediaRequest> requests = await db.MediaRequests.AsNoTracking()
            .Where(request => request.KidProfileId == profileId)
            .ToListAsync(ct);
        requests.Sort(static (left, right) =>
        {
            int byTime = right.LastRequestedAt.CompareTo(left.LastRequestedAt);
            return byTime != 0 ? byTime : right.Id.CompareTo(left.Id);
        });

        Dictionary<int, string> notes = await this.ParentNotesAsync(requests, ct);
        string? serverId = requests.Any(request => ToWishState(request.State) == KidWishState.Ready)
            ? await this.TryServerIdAsync(ct)
            : null;

        List<KidWishItem> items = [];
        foreach (MediaRequest request in requests)
        {
            if (ToWishState(request.State) is not KidWishState state)
            {
                continue;
            }

            KidPlexLinks? plex = state == KidWishState.Ready
                ? await this.LinksForAsync(request, serverId, ct)
                : null;
            items.Add(new KidWishItem(
                request.Id,
                request.Kind,
                request.TmdbId,
                string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim(),
                state,
                state == KidWishState.Denied && notes.TryGetValue(request.Id, out string? note) ? note : null,
                plex));
        }

        KidAllowanceSnapshot allowance = await flow.AllowanceAsync(profile, ct);
        return new KidWishlistResponse(allowance.Remaining, allowance.ResetsAt, items);
    }

    internal static KidWishState? ToWishState(RequestState state) => state switch
    {
        RequestState.Pending or RequestState.Failed => KidWishState.Waiting,
        RequestState.Approved or RequestState.Fulfilling => KidWishState.OnTheWay,
        RequestState.Available or RequestState.Labeled => KidWishState.Ready,
        RequestState.Denied => KidWishState.Denied,
        _ => null,
    };

    private async Task<Dictionary<int, string>> ParentNotesAsync(List<MediaRequest> requests, CancellationToken ct)
    {
        List<int> deniedIds = [.. requests.Where(request => request.State == RequestState.Denied).Select(request => request.Id)];
        Dictionary<int, string> notes = [];
        if (deniedIds.Count == 0)
        {
            return notes;
        }

        List<MediaRequestEvent> events = await db.MediaRequestEvents.AsNoTracking()
            .Where(item => deniedIds.Contains(item.MediaRequestId)
                && item.ToState == RequestState.Denied
                && item.ActorKind == RequestActorKind.Parent
                && item.Reason != null)
            .OrderByDescending(item => item.Id)
            .ToListAsync(ct);
        foreach (MediaRequestEvent item in events)
        {
            if (notes.ContainsKey(item.MediaRequestId) || string.IsNullOrWhiteSpace(item.Reason))
            {
                continue;
            }

            notes[item.MediaRequestId] = item.Reason.Trim();
        }

        return notes;
    }

    private async Task<string?> TryServerIdAsync(CancellationToken ct)
    {
        try
        {
            return await scanner.GetServerIdAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is MediaServerException or HttpRequestException)
        {
            return null;
        }
    }

    private async Task<KidPlexLinks?> LinksForAsync(MediaRequest request, string? serverId, CancellationToken ct)
    {
        if (!PlexWatchLink.IsMachineId(serverId))
        {
            return null;
        }

        ExternalId id = new(ExternalIdSource.Tmdb, request.TmdbId.ToString(CultureInfo.InvariantCulture));
        foreach (ILibraryIndex index in indexes)
        {
            IReadOnlyList<MediaItem>? found;
            try
            {
                found = await index.FindAsync(request.Kind, id, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                this.LogLookupFailed(ex, request.Kind, request.TmdbId);
                return null;
            }

            if (found is null)
            {
                continue;
            }

            foreach (MediaItem item in found)
            {
                if (item.Kind == request.Kind && PlexWatchLink.TryCreate(serverId, item.Id, request.Kind) is KidPlexLinks links)
                {
                    return links;
                }
            }

            return null;
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wishlist could not look up {Kind} {TmdbId}; no Plex link was added")]
    private partial void LogLookupFailed(Exception ex, MediaKind kind, int tmdbId);
}
