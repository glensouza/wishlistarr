using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;

namespace Wishlistarr.Tests.Requests;

public sealed class AutoApproveRuleTests
{
    [Fact]
    public void A_switch_approves_allowed_and_unrated_titles_and_stops_at_blocks_and_unknown_genres()
    {
        AutoApproveRule rule = new(true, null, null);

        Assert.True(rule.AppliesTo(Decision(ContentOutcome.Allowed, ContentReasonCode.NoRatingLimit, RatingLevel.Teen), MediaKind.Movie, RatingLevel.Teen));
        Assert.True(rule.AppliesTo(Decision(ContentOutcome.NeedsApproval, ContentReasonCode.Unrated, null), MediaKind.Show, null));
        Assert.False(rule.AppliesTo(Decision(ContentOutcome.Blocked, ContentReasonCode.AboveRatingLimit, RatingLevel.Mature), MediaKind.Movie, RatingLevel.Mature));
        Assert.False(rule.AppliesTo(Decision(ContentOutcome.NeedsApproval, ContentReasonCode.UnknownGenres, null), MediaKind.Movie, RatingLevel.General));
        Assert.False(rule.AppliesTo(Decision(ContentOutcome.NeedsApproval, ContentReasonCode.Unrated, null), MediaKind.Movie, null, genresUnknown: true));
    }

    [Fact]
    public void A_rating_ceiling_approves_only_known_ratings_at_or_below_it_for_that_kind()
    {
        AutoApproveRule rule = new(false, RatingLevel.ParentalGuidance, RatingLevel.Children);
        ContentDecision allowed = Decision(ContentOutcome.Allowed, ContentReasonCode.NoRatingLimit, RatingLevel.ParentalGuidance);

        Assert.True(rule.AppliesTo(allowed, MediaKind.Movie, RatingLevel.ParentalGuidance));
        Assert.True(rule.AppliesTo(allowed, MediaKind.Movie, RatingLevel.General));
        Assert.False(rule.AppliesTo(allowed, MediaKind.Movie, RatingLevel.Teen));
        Assert.False(rule.AppliesTo(allowed, MediaKind.Movie, null));
        Assert.False(rule.AppliesTo(allowed, MediaKind.Show, RatingLevel.ParentalGuidance));
        Assert.True(rule.AppliesTo(allowed, MediaKind.Show, RatingLevel.Children));
        Assert.False(rule.AppliesTo(
            Decision(ContentOutcome.NeedsApproval, ContentReasonCode.UnknownGenres, null),
            MediaKind.Movie,
            RatingLevel.General));
        Assert.False(rule.AppliesTo(
            Decision(ContentOutcome.Blocked, ContentReasonCode.BlockedGenre, RatingLevel.General),
            MediaKind.Movie,
            RatingLevel.General));
    }

    [Fact]
    public void No_rule_approves_nothing()
    {
        AutoApproveRule rule = new(false, null, null);

        Assert.False(rule.AppliesTo(Decision(ContentOutcome.Allowed, ContentReasonCode.WithinRatingLimit, RatingLevel.General), MediaKind.Movie, RatingLevel.General));
    }

    private static ContentDecision Decision(ContentOutcome outcome, ContentReasonCode code, RatingLevel? level) =>
        new(outcome, new ContentReason(code, Level: level));
}
