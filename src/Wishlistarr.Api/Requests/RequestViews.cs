using Microsoft.EntityFrameworkCore;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Requests;

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
}
