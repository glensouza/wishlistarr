namespace Wishlistarr.Core.Content;

public sealed class OriginalLanguageRule : IContentRule
{
    public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy)
    {
        HashSet<string> allowed = [];
        foreach (string language in policy.AllowedOriginalLanguages)
        {
            if (ContentLanguages.Canonical(language) is string code)
            {
                allowed.Add(code);
            }
        }

        if (allowed.Count == 0)
        {
            return new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoLanguageLimit));
        }

        string? original = ContentLanguages.Canonical(title.OriginalLanguage);
        if (original is not null && allowed.Contains(original))
        {
            return new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.LanguageAllowed, OriginalLanguage: original));
        }

        return new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.LanguageNotAllowed, OriginalLanguage: original));
    }
}
