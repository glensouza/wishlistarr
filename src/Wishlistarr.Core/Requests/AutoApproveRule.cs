using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Requests;

public enum AutoApproveSkip
{
    NotAllowed,
    GenresUnknown,
    Unrated,
    AboveCeiling,
    SwitchOff,
}

public sealed record AutoApproveRule(bool Enabled, RatingLevel? MaxMovieRating, RatingLevel? MaxTvRating)
{
    public bool AppliesTo(ContentDecision decision, MediaKind kind, RatingLevel? rating, bool genresUnknown = false) =>
        this.WhyNot(decision, kind, rating, genresUnknown) is null;

    public AutoApproveSkip? WhyNot(ContentDecision decision, MediaKind kind, RatingLevel? rating, bool genresUnknown = false)
    {
        if (genresUnknown)
        {
            return AutoApproveSkip.GenresUnknown;
        }

        if (decision.Outcome != ContentOutcome.Allowed)
        {
            return AutoApproveSkip.NotAllowed;
        }

        if (rating is not RatingLevel level)
        {
            return AutoApproveSkip.Unrated;
        }

        RatingLevel? ceiling = kind == MediaKind.Movie ? this.MaxMovieRating : this.MaxTvRating;
        if (ceiling is RatingLevel max)
        {
            return level <= max ? null : AutoApproveSkip.AboveCeiling;
        }

        return this.Enabled ? null : AutoApproveSkip.SwitchOff;
    }
}
