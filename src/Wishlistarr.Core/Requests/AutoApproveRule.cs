using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Requests;

public sealed record AutoApproveRule(bool Enabled, RatingLevel? MaxMovieRating, RatingLevel? MaxTvRating)
{
    public bool AppliesTo(ContentDecision decision, MediaKind kind, RatingLevel? rating, bool genresUnknown = false)
    {
        if (genresUnknown || decision.Outcome != ContentOutcome.Allowed || rating is not RatingLevel level)
        {
            return false;
        }

        RatingLevel? ceiling = kind == MediaKind.Movie ? this.MaxMovieRating : this.MaxTvRating;
        if (ceiling is RatingLevel max)
        {
            return level <= max;
        }

        return this.Enabled;
    }
}
