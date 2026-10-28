namespace Wishlistarr.Core.Content;

public sealed class DescriptorBlockRule : IContentRule
{
    public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy) =>
        AdvisoryText.FirstMatch(title.Descriptors, policy.BlockedDescriptors) is string descriptor
            ? new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.BlockedDescriptor, Descriptor: descriptor))
            : new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoBlockedDescriptor));
}
