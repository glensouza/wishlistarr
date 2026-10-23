using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Requests;

public sealed record AutoApproveRule(bool Enabled, RatingLevel? MaxMovieRating, RatingLevel? MaxTvRating)
{
    public bool AppliesTo(ContentDecision decision, MediaKind kind, RatingLevel? rating, bool genresUnknown = false)
    {
        if (genresUnknown || decision.Outcome == ContentOutcome.Blocked || decision.Reason.Code == ContentReasonCode.UnknownGenres)
        {
            return false;
        }

        if (this.Enabled)
        {
            return true;
        }

        RatingLevel? ceiling = kind == MediaKind.Movie ? this.MaxMovieRating : this.MaxTvRating;
        return rating is RatingLevel level && ceiling is RatingLevel max && level <= max;
    }
}
