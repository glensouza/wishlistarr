namespace Wishlistarr.Core.Content;

public sealed class ContentPolicyEngine
{
    private readonly IReadOnlyList<IContentRule> rules;

    public ContentPolicyEngine(IReadOnlyList<IContentRule> rules)
    {
        if (rules.Count == 0)
        {
            throw new ArgumentException("At least one rule is required.", nameof(rules));
        }

        this.rules = [.. rules];
    }

    public static ContentPolicyEngine Default { get; } = new([
        new RatingRule(),
        new GenreBlockRule(),
        new KeywordBlockRule(),
        new OriginalLanguageRule(),
        new DescriptorBlockRule(),
        new TopicBlockRule(),
    ]);

    public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy)
    {
        if (policy.Blocked.Contains(title.Key))
        {
            return new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.ExplicitlyBlocked));
        }

        if (policy.Allowed.Contains(title.Key))
        {
            return new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.ExplicitlyAllowed));
        }

        ContentDecision decision = this.rules[0].Evaluate(title, policy);
        foreach (IContentRule rule in this.rules.Skip(1))
        {
            ContentDecision next = rule.Evaluate(title, policy);
            if (next.Outcome > decision.Outcome)
            {
                decision = next;
            }
        }

        return decision;
    }
}
