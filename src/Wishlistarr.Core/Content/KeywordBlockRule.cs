namespace Wishlistarr.Core.Content;

public sealed class KeywordBlockRule : IContentRule
{
    public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy)
    {
        int? blocked = FirstBlocked(title.KeywordIds, policy.BlockedKeywordIds);
        return blocked is int keywordId
            ? new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.BlockedKeyword, KeywordId: keywordId))
            : new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoBlockedKeyword));
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
