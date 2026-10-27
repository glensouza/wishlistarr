using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;

namespace Wishlistarr.Tests.Requests;

public sealed class AutoApproveRuleTests
{
    [Fact]
    public void WhyNot_names_the_reason_AppliesTo_rejects()
    {
        ContentDecision allowed = Decision(ContentOutcome.Allowed, ContentReasonCode.WithinRatingLimit, RatingLevel.Teen);
        AutoApproveRule ceiling = new(false, RatingLevel.ParentalGuidance, null);

        Assert.Null(ceiling.WhyNot(allowed, MediaKind.Movie, RatingLevel.General));
        Assert.True(ceiling.AppliesTo(allowed, MediaKind.Movie, RatingLevel.General));
        Assert.Equal(AutoApproveSkip.AboveCeiling, ceiling.WhyNot(allowed, MediaKind.Movie, RatingLevel.Teen));
        Assert.Equal(AutoApproveSkip.SwitchOff, new AutoApproveRule(false, null, null).WhyNot(allowed, MediaKind.Movie, RatingLevel.General));
        Assert.Equal(AutoApproveSkip.Unrated, new AutoApproveRule(true, null, null).WhyNot(allowed, MediaKind.Movie, null));
        Assert.Equal(AutoApproveSkip.GenresUnknown, new AutoApproveRule(true, null, null).WhyNot(allowed, MediaKind.Movie, RatingLevel.General, genresUnknown: true));
        Assert.Equal(
            AutoApproveSkip.NotAllowed,
            new AutoApproveRule(true, null, null).WhyNot(
                Decision(ContentOutcome.Blocked, ContentReasonCode.BlockedGenre, RatingLevel.General),
                MediaKind.Movie,
                RatingLevel.General));
    }

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

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Show)]
    public void A_ceiling_with_the_switch_off_approves_exactly_the_levels_at_or_below_it(MediaKind kind)
    {
        foreach (RatingLevel ceiling in Enum.GetValues<RatingLevel>())
        {
            AutoApproveRule rule = kind == MediaKind.Movie ? new(false, ceiling, null) : new(false, null, ceiling);
            foreach (RatingLevel rating in Enum.GetValues<RatingLevel>())
            {
                Assert.Equal(rating <= ceiling, rule.AppliesTo(Allowed(rating), kind, rating));
            }
        }
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Show)]
    public void The_switch_alone_approves_every_rated_level(MediaKind kind)
    {
        AutoApproveRule rule = new(true, null, null);

        Assert.All(Enum.GetValues<RatingLevel>(), rating => Assert.True(rule.AppliesTo(Allowed(rating), kind, rating)));
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
