namespace Wishlistarr.Core.Content;

public sealed class TopicBlockRule : IContentRule
{
    public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy) =>
        AdvisoryText.FirstMatch(title.Topics, policy.BlockedTopics) is string topic
            ? new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.BlockedTopic, Topic: topic))
            : new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoBlockedTopic));
}
