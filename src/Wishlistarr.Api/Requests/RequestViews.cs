using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

public enum KidHistoryAction
{
    Granted,
    Asked,
    Approved,
    AutoApproved,
    Denied,
    Ready,
    Revoked,
    Failed,
}

public sealed record KidHistoryEntry(
    int Id,
    int RequestId,
    MediaKind Kind,
    int TmdbId,
    string Title,
    KidHistoryAction Action,
    DateTimeOffset At,
    string? Note);

public static class RequestViews
{
    public static bool TryNormalizeNote(string? note, out string? normalized, out string? error)
    {
        normalized = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (normalized is { Length: > MediaRequests.MaxReasonLength })
        {
            error = $"Reasons are limited to {MediaRequests.MaxReasonLength} characters.";
            normalized = null;
            return false;
        }

        error = null;
        return true;
    }

    public static async Task<Dictionary<int, string>> DenialReasonsAsync(
        WishlistarrDbContext db,
        IReadOnlyCollection<int> requestIds,
        CancellationToken ct)
    {
        Dictionary<int, string> reasons = [];
        if (requestIds.Count == 0)
        {
            return reasons;
        }

        List<MediaRequestEvent> events = await db.MediaRequestEvents.AsNoTracking()
            .Where(item => requestIds.Contains(item.MediaRequestId)
                && item.FromState == RequestState.Pending
                && item.ToState == RequestState.Denied
                && item.Reason != null)
            .OrderBy(item => item.Id)
            .ToListAsync(ct);
        foreach (MediaRequestEvent item in events)
        {
            if (!string.IsNullOrWhiteSpace(item.Reason))
            {
                reasons[item.MediaRequestId] = item.Reason;
            }
        }

        return reasons;
    }

    // A write that timed out or threw may still have reached Plex, so only those failures can have left a label behind.
    public static async Task<HashSet<int>> LabelMayRemainAsync(
        WishlistarrDbContext db,
        IReadOnlyCollection<int> failedIds,
        CancellationToken ct)
    {
        if (failedIds.Count == 0)
        {
            return [];
        }

        return [.. (await LastFailuresAsync(db, failedIds, ct))
            .Where(pair => pair.Value.Reason is RequestLabeling.TimeoutReason or RequestLabeling.WriteFailedReason)
            .Select(pair => pair.Key)];
    }

    public static async Task<Dictionary<int, MediaRequestEvent>> LastFailuresAsync(
        WishlistarrDbContext db,
        IReadOnlyCollection<int> requestIds,
        CancellationToken ct)
    {
        Dictionary<int, MediaRequestEvent> last = [];
        if (requestIds.Count == 0)
        {
            return last;
        }

        List<MediaRequestEvent> events = await db.MediaRequestEvents.AsNoTracking()
            .Where(item => requestIds.Contains(item.MediaRequestId) && item.ToState == RequestState.Failed)
            .OrderBy(item => item.Id)
            .ToListAsync(ct);
        foreach (MediaRequestEvent item in events)
        {
            last[item.MediaRequestId] = item;
        }

        return last;
    }

    public static async Task<IReadOnlyList<KidHistoryEntry>> KidHistoryAsync(
        WishlistarrDbContext db,
        int profileId,
        RequestTitles titles,
        int limit,
        int offset,
        CancellationToken ct)
    {
        List<MediaRequestEvent> events = await db.MediaRequestEvents.AsNoTracking()
            .Where(item => db.MediaRequests.Any(request => request.Id == item.MediaRequestId && request.KidProfileId == profileId)
                && item.ToState != RequestState.Available
                && item.ToState != RequestState.Fulfilling)
            .OrderByDescending(item => item.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
        if (events.Count == 0)
        {
            return [];
        }

        int[] requestIds = [.. events.Select(item => item.MediaRequestId).Distinct()];
        Dictionary<int, MediaRequest> byId = await db.MediaRequests.AsNoTracking()
            .Where(request => requestIds.Contains(request.Id) && request.KidProfileId == profileId)
            .ToDictionaryAsync(request => request.Id, ct);
        IReadOnlyDictionary<int, string> names = await titles.GetManyAsync(byId.Values, ct);
        List<KidHistoryEntry> history = [];
        foreach (MediaRequestEvent item in events)
        {
            if (!byId.TryGetValue(item.MediaRequestId, out MediaRequest? request) || ActionFor(item) is not KidHistoryAction action)
            {
                continue;
            }

            string? note = action == KidHistoryAction.Denied && !string.IsNullOrWhiteSpace(item.Reason) ? item.Reason : null;
            history.Add(new KidHistoryEntry(item.Id, request.Id, request.Kind, request.TmdbId, names[request.Id], action, item.OccurredAt, note));
        }

        return history;
    }

    private static KidHistoryAction? ActionFor(MediaRequestEvent item)
    {
        if (item.FromState is null && item.ToState == RequestState.Pending)
        {
            return item.ActorKind == RequestActorKind.Parent ? KidHistoryAction.Granted : KidHistoryAction.Asked;
        }

        return item.ToState switch
        {
            RequestState.Approved => item.ActorKind == RequestActorKind.Parent ? KidHistoryAction.Approved : KidHistoryAction.AutoApproved,
            RequestState.Denied => KidHistoryAction.Denied,
            RequestState.Labeled => KidHistoryAction.Ready,
            RequestState.Revoked => KidHistoryAction.Revoked,
            RequestState.Failed => KidHistoryAction.Failed,
            _ => null,
        };
    }
}
