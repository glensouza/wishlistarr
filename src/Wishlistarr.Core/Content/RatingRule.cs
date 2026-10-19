namespace Wishlistarr.Core.Content;

public sealed class RatingRule : IContentRule
{
    public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy)
    {
        RatingLevel? max = policy.MaxRatingFor(title.Key.Kind);
        if (max is null)
        {
            return new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoRatingLimit));
        }

        RegionCertification? unrecognized = null;
        foreach (string region in RegionOrder(policy.Region))
        {
            foreach (RegionCertification certification in title.Certifications.Where(item => item.Region.Trim().Equals(region, StringComparison.OrdinalIgnoreCase)))
            {
                if (RatingScale.TryParse(region, certification.Rating, out RatingLevel? level))
                {
                    bool within = level <= max;
                    return new ContentDecision(
                        within ? ContentOutcome.Allowed : ContentOutcome.Blocked,
                        new ContentReason(
                            within ? ContentReasonCode.WithinRatingLimit : ContentReasonCode.AboveRatingLimit,
                            region,
                            certification.Rating.Trim(),
                            level,
                            max));
                }

                unrecognized ??= certification;
            }
        }

        ContentOutcome outcome = policy.Unrated switch
        {
            UnratedPolicy.Allow => ContentOutcome.Allowed,
            UnratedPolicy.Block => ContentOutcome.Blocked,
            _ => ContentOutcome.NeedsApproval,
        };
        return new ContentDecision(
            outcome,
            new ContentReason(ContentReasonCode.Unrated, unrecognized?.Region.Trim().ToUpperInvariant(), unrecognized?.Rating.Trim(), MaxLevel: max));
    }

    private static IEnumerable<string> RegionOrder(string? configured)
    {
        yield return RatingScale.UsRegion;
        string region = configured?.Trim().ToUpperInvariant() ?? string.Empty;
        if (region.Length > 0 && region != RatingScale.UsRegion)
        {
            yield return region;
        }
    }
}
