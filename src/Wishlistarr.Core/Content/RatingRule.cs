namespace Wishlistarr.Core.Content;

public sealed class RatingRule : IContentRule
{
    public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy)
    {
        RatingLevel? max = policy.MaxRatingFor(title.Key.Kind);
        IEnumerable<RegionCertification> certifications = (title.Certifications ?? []).OfType<RegionCertification>();
        RegionCertification? unrecognized = null;
        foreach (string region in RegionOrder(policy.Region))
        {
            RegionCertification? strictest = null;
            RatingLevel strictestLevel = default;
            foreach (RegionCertification certification in certifications.Where(item => Normalize(item.Region) == region))
            {
                if (!RatingScale.TryParse(region, certification.Rating, out RatingLevel? level))
                {
                    unrecognized ??= certification;
                }
                else if (strictest is null || level > strictestLevel)
                {
                    strictest = certification;
                    strictestLevel = level.Value;
                }
            }

            if (strictest is null)
            {
                continue;
            }

            string rating = strictest.Rating.Trim();
            if (max is null)
            {
                return new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoRatingLimit, region, rating, strictestLevel));
            }

            bool within = strictestLevel <= max;
            return new ContentDecision(
                within ? ContentOutcome.Allowed : ContentOutcome.Blocked,
                new ContentReason(within ? ContentReasonCode.WithinRatingLimit : ContentReasonCode.AboveRatingLimit, region, rating, strictestLevel, max));
        }

        ContentOutcome outcome = policy.Unrated switch
        {
            UnratedPolicy.Allow => ContentOutcome.Allowed,
            UnratedPolicy.Block => ContentOutcome.Blocked,
            _ => ContentOutcome.NeedsApproval,
        };
        return new ContentDecision(
            outcome,
            new ContentReason(ContentReasonCode.Unrated, unrecognized is null ? null : Normalize(unrecognized.Region), unrecognized?.Rating?.Trim(), MaxLevel: max));
    }

    private static string Normalize(string? region) => region?.Trim().ToUpperInvariant() ?? string.Empty;

    private static IEnumerable<string> RegionOrder(string? configured)
    {
        yield return RatingScale.UsRegion;
        string region = Normalize(configured);
        if (region.Length > 0 && region != RatingScale.UsRegion)
        {
            yield return region;
        }
    }
}
