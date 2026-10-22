namespace Wishlistarr.Core.Content;

public sealed class GenreBlockRule : IContentRule
{
    public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy)
    {
        int? blocked = FirstBlocked(title.GenreIds, policy.BlockedGenreIds);
        return blocked is int genreId
            ? new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.BlockedGenre, GenreId: genreId))
            : new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoBlockedGenre));
    }

    private static int? FirstBlocked(IReadOnlyList<int>? ids, IReadOnlySet<int> blocked)
    {
        if (ids is null || blocked.Count == 0)
        {
            return null;
        }

        foreach (int id in ids)
        {
            if (blocked.Contains(id))
            {
                return id;
            }
        }

        return null;
    }
}
