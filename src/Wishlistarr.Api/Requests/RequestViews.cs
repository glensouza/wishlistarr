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

    public static async Task<IReadOnlyList<KidHistoryEntry>> KidHistoryAsync(
        WishlistarrDbContext db,
        int profileId,
        RequestTitles titles,
        CancellationToken ct)
    {
        List<MediaRequest> requests = await db.MediaRequests.AsNoTracking()
            .Where(request => request.KidProfileId == profileId)
            .ToListAsync(ct);
        if (requests.Count == 0)
        {
            return [];
        }

        Dictionary<int, MediaRequest> byId = requests.ToDictionary(request => request.Id);
        int[] requestIds = [.. byId.Keys];
        List<MediaRequestEvent> events = await db.MediaRequestEvents.AsNoTracking()
            .Where(item => requestIds.Contains(item.MediaRequestId))
            .OrderByDescending(item => item.Id)
            .ToListAsync(ct);
        Dictionary<int, string> names = [];
        List<KidHistoryEntry> history = [];
        foreach (MediaRequestEvent item in events)
        {
            if (!byId.TryGetValue(item.MediaRequestId, out MediaRequest? request))
            {
                continue;
            }

            if (ActionFor(item) is not KidHistoryAction action)
            {
                continue;
            }

            if (!names.TryGetValue(request.Id, out string? title))
            {
                title = await titles.GetAsync(request, ct);
                names[request.Id] = title;
            }

            string? note = action == KidHistoryAction.Denied && !string.IsNullOrWhiteSpace(item.Reason) ? item.Reason : null;
            history.Add(new KidHistoryEntry(item.Id, request.Id, request.Kind, request.TmdbId, title, action, item.OccurredAt, note));
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
            RequestState.Approved => KidHistoryAction.Approved,
            RequestState.Denied => KidHistoryAction.Denied,
            RequestState.Labeled => KidHistoryAction.Ready,
            RequestState.Revoked => KidHistoryAction.Revoked,
            RequestState.Failed => KidHistoryAction.Failed,
            _ => null,
        };
    }
}
