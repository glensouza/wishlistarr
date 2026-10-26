using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;

namespace Wishlistarr.Tests.Requests;

public sealed class AutoApproveRuleTests
{
    [Fact]
    public void A_switch_approves_allowed_rated_titles_and_never_unrated_ones()
    {
        AutoApproveRule rule = new(true, null, null);

        Assert.True(rule.AppliesTo(Decision(ContentOutcome.Allowed, ContentReasonCode.NoRatingLimit, RatingLevel.Teen), MediaKind.Movie, RatingLevel.Teen));
        Assert.False(rule.AppliesTo(Decision(ContentOutcome.NeedsApproval, ContentReasonCode.Unrated, null), MediaKind.Show, null));
        Assert.False(rule.AppliesTo(Decision(ContentOutcome.Allowed, ContentReasonCode.Unrated, null), MediaKind.Movie, null));
        Assert.False(rule.AppliesTo(Decision(ContentOutcome.NeedsApproval, ContentReasonCode.LanguageNotAllowed, null), MediaKind.Movie, RatingLevel.General));
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

    [Theory]
    [InlineData(RatingLevel.General, true)]
    [InlineData(RatingLevel.ParentalGuidance, true)]
    [InlineData(RatingLevel.Teen, false)]
    public void A_ceiling_caps_the_switch_for_movies(RatingLevel rating, bool expected)
    {
        AutoApproveRule rule = new(true, RatingLevel.ParentalGuidance, null);

        Assert.Equal(expected, rule.AppliesTo(Allowed(rating), MediaKind.Movie, rating));
    }

    [Fact]
    public void Movie_and_tv_ceilings_are_independent_with_the_switch_on()
    {
        AutoApproveRule rule = new(true, RatingLevel.Teen, RatingLevel.Children);

        Assert.True(rule.AppliesTo(Allowed(RatingLevel.Teen), MediaKind.Movie, RatingLevel.Teen));
        Assert.False(rule.AppliesTo(Allowed(RatingLevel.Teen), MediaKind.Show, RatingLevel.Teen));
        Assert.True(rule.AppliesTo(Allowed(RatingLevel.Children), MediaKind.Show, RatingLevel.Children));
        Assert.False(rule.AppliesTo(Allowed(RatingLevel.Mature), MediaKind.Movie, RatingLevel.Mature));
    }

    [Fact]
    public void A_missing_ceiling_for_one_kind_leaves_that_kind_to_the_switch()
    {
        AutoApproveRule rule = new(true, RatingLevel.General, null);

        Assert.True(rule.AppliesTo(Allowed(RatingLevel.Mature), MediaKind.Show, RatingLevel.Mature));
        Assert.False(rule.AppliesTo(Allowed(RatingLevel.Mature), MediaKind.Movie, RatingLevel.Mature));
        Assert.False(rule.AppliesTo(Allowed(RatingLevel.General), MediaKind.Movie, null));
    }

    [Fact]
    public void No_rule_approves_nothing()
    {
        AutoApproveRule rule = new(false, null, null);

        Assert.False(rule.AppliesTo(Decision(ContentOutcome.Allowed, ContentReasonCode.WithinRatingLimit, RatingLevel.General), MediaKind.Movie, RatingLevel.General));
    }

    private static ContentDecision Allowed(RatingLevel level) =>
        Decision(ContentOutcome.Allowed, ContentReasonCode.NoRatingLimit, level);

    private static ContentDecision Decision(ContentOutcome outcome, ContentReasonCode code, RatingLevel? level) =>
        new(outcome, new ContentReason(code, Level: level));
}
