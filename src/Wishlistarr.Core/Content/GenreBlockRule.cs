namespace Wishlistarr.Core.Content;

public sealed class GenreBlockRule : IContentRule
{
    public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy)
    {
        if (policy.BlockedGenreIds.Count > 0 && title.GenreIds is not { Count: > 0 })
        {
            return new ContentDecision(ContentOutcome.NeedsApproval, new ContentReason(ContentReasonCode.UnknownGenres));
        }

        return BlockedIds.First(title.GenreIds, policy.BlockedGenreIds) is int genreId
            ? new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.BlockedGenre, GenreId: genreId))
            : new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoBlockedGenre));
    }
}
