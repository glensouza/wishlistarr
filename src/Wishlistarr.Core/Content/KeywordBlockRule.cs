namespace Wishlistarr.Core.Content;

public sealed class KeywordBlockRule : IContentRule
{
    public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy) =>
        BlockedIds.First(title.KeywordIds, policy.BlockedKeywordIds) is int keywordId
            ? new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.BlockedKeyword, KeywordId: keywordId))
            : new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoBlockedKeyword));
}
