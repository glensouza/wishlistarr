namespace Wishlistarr.Core.Content;

public sealed class OriginalLanguageRule : IContentRule
{
    public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy)
    {
        if (policy.AllowedOriginalLanguages.Count == 0)
        {
            return new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoLanguageLimit));
        }

        string? original = ContentLanguages.Canonical(title.OriginalLanguage);
        if (original is not null && policy.AllowedOriginalLanguages.Contains(original))
        {
            return new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.LanguageAllowed, OriginalLanguage: original));
        }

        return new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.LanguageNotAllowed, OriginalLanguage: original));
    }
}
