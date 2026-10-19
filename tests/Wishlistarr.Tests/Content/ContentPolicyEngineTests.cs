using System.Collections.Immutable;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Tests.Content;

public sealed class ContentPolicyEngineTests
{
    private static readonly TitleKey Movie = new(MediaKind.Movie, 603);
    private static readonly TitleKey Show = new(MediaKind.Show, 1399);

    [Theory]
    [InlineData("G", RatingLevel.General)]
    [InlineData("PG", RatingLevel.ParentalGuidance)]
    [InlineData("PG-13", RatingLevel.Teen)]
    [InlineData("R", RatingLevel.Mature)]
    [InlineData("NC-17", RatingLevel.Adult)]
    [InlineData("TV-Y", RatingLevel.Preschool)]
    [InlineData("TV-Y7", RatingLevel.Children)]
    [InlineData("TV-G", RatingLevel.General)]
    [InlineData("TV-PG", RatingLevel.ParentalGuidance)]
    [InlineData("TV-14", RatingLevel.Teen)]
    [InlineData("TV-MA", RatingLevel.Mature)]
    public void Us_ratings_map_to_the_shared_scale(string rating, RatingLevel expected)
    {
        Assert.True(RatingScale.TryParse("US", rating, out RatingLevel? level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("G", "TV-G")]
    [InlineData("PG", "TV-PG")]
    [InlineData("PG-13", "TV-14")]
    [InlineData("R", "TV-MA")]
    public void Equivalent_movie_and_tv_ratings_compare_equal(string movie, string tv)
    {
        RatingScale.TryParse("US", movie, out RatingLevel? movieLevel);
        RatingScale.TryParse("US", tv, out RatingLevel? tvLevel);
        Assert.Equal(movieLevel, tvLevel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NR")]
    [InlineData("Not Rated")]
    [InlineData("12")]
    public void Unknown_us_ratings_are_not_recognized(string rating) => Assert.False(RatingScale.TryParse("US", rating, out _));

    [Fact]
    public void Rating_within_the_limit_is_allowed_with_the_data_used()
    {
        ContentDecision decision = Evaluate(Title(Movie, ("US", "PG")), new ContentPolicy { MaxMovieRating = RatingLevel.ParentalGuidance });

        Assert.Equal(ContentOutcome.Allowed, decision.Outcome);
        Assert.Equal(
            new ContentReason(ContentReasonCode.WithinRatingLimit, "US", "PG", RatingLevel.ParentalGuidance, RatingLevel.ParentalGuidance),
            decision.Reason);
    }

    [Fact]
    public void Rating_above_the_limit_is_blocked_with_the_data_used()
    {
        ContentDecision decision = Evaluate(Title(Movie, ("US", "PG-13")), new ContentPolicy { MaxMovieRating = RatingLevel.ParentalGuidance });

        Assert.Equal(ContentOutcome.Blocked, decision.Outcome);
        Assert.Equal(
            new ContentReason(ContentReasonCode.AboveRatingLimit, "US", "PG-13", RatingLevel.Teen, RatingLevel.ParentalGuidance),
            decision.Reason);
    }

    [Fact]
    public void Movies_and_shows_use_their_own_limits()
    {
        ContentPolicy policy = new() { MaxMovieRating = RatingLevel.Teen, MaxTvRating = RatingLevel.Children };

        Assert.Equal(ContentOutcome.Allowed, Evaluate(Title(Movie, ("US", "PG")), policy).Outcome);
        Assert.Equal(ContentOutcome.Blocked, Evaluate(Title(Show, ("US", "TV-PG")), policy).Outcome);
    }

    [Fact]
    public void Shows_are_decided_by_the_series_key()
    {
        TitleKey sameIdAsMovie = new(MediaKind.Show, Movie.TmdbId);
        ContentPolicy policy = new() { MaxTvRating = RatingLevel.Mature, Blocked = ImmutableHashSet.Create(Show) };

        Assert.Equal(ContentOutcome.Blocked, Evaluate(Title(Show, ("US", "TV-Y")), policy).Outcome);
        Assert.Equal(ContentOutcome.Allowed, Evaluate(Title(sameIdAsMovie, ("US", "TV-Y")), policy).Outcome);
    }

    [Fact]
    public void Missing_certification_needs_approval_by_default()
    {
        ContentDecision decision = Evaluate(Title(Movie), new ContentPolicy { MaxMovieRating = RatingLevel.Mature });

        Assert.Equal(ContentOutcome.NeedsApproval, decision.Outcome);
        Assert.Equal(new ContentReason(ContentReasonCode.Unrated, MaxLevel: RatingLevel.Mature), decision.Reason);
    }

    [Theory]
    [InlineData(UnratedPolicy.Allow, ContentOutcome.Allowed)]
    [InlineData(UnratedPolicy.NeedsApproval, ContentOutcome.NeedsApproval)]
    [InlineData(UnratedPolicy.Block, ContentOutcome.Blocked)]
    public void Unrated_policy_is_configurable(UnratedPolicy unrated, ContentOutcome expected)
    {
        ContentPolicy policy = new() { MaxTvRating = RatingLevel.General, Unrated = unrated };

        Assert.Equal(expected, Evaluate(Title(Show), policy).Outcome);
        Assert.Equal(expected, Evaluate(Title(Show, ("US", "NR")), policy).Outcome);
    }

    [Fact]
    public void Unrecognized_rating_is_reported_as_unrated()
    {
        ContentDecision decision = Evaluate(Title(Movie, ("us", " NR ")), new ContentPolicy { MaxMovieRating = RatingLevel.General });

        Assert.Equal(new ContentReason(ContentReasonCode.Unrated, "US", "NR", MaxLevel: RatingLevel.General), decision.Reason);
    }

    [Fact]
    public void Us_rating_is_preferred_over_the_configured_region()
    {
        ContentPolicy policy = new() { MaxMovieRating = RatingLevel.ParentalGuidance, Region = "GB" };

        ContentDecision decision = Evaluate(Title(Movie, ("GB", "18"), ("US", "PG")), policy);

        Assert.Equal(ContentOutcome.Allowed, decision.Outcome);
        Assert.Equal("US", decision.Reason.Region);
    }

    [Fact]
    public void Configured_region_is_used_when_us_is_missing_or_unrated()
    {
        ContentPolicy policy = new() { MaxMovieRating = RatingLevel.ParentalGuidance, Region = "gb" };

        ContentDecision missing = Evaluate(Title(Movie, ("GB", "U")), policy);
        ContentDecision unrated = Evaluate(Title(Movie, ("US", "NR"), ("GB", "15")), policy);

        Assert.Equal(new ContentReason(ContentReasonCode.WithinRatingLimit, "GB", "U", RatingLevel.General, RatingLevel.ParentalGuidance), missing.Reason);
        Assert.Equal(new ContentReason(ContentReasonCode.AboveRatingLimit, "GB", "15", RatingLevel.Mature, RatingLevel.ParentalGuidance), unrated.Reason);
    }

    [Fact]
    public void Other_regions_fall_through_to_the_unrated_policy()
    {
        ContentPolicy policy = new() { MaxMovieRating = RatingLevel.Mature, Region = "GB" };

        ContentDecision decision = Evaluate(Title(Movie, ("DE", "0"), ("FR", "U")), policy);

        Assert.Equal(ContentOutcome.NeedsApproval, decision.Outcome);
        Assert.Equal(ContentReasonCode.Unrated, decision.Reason.Code);
    }

    [Theory]
    [InlineData("DE", "0", RatingLevel.General)]
    [InlineData("DE", "6", RatingLevel.ParentalGuidance)]
    [InlineData("DE", "12", RatingLevel.Teen)]
    [InlineData("DE", "16", RatingLevel.Mature)]
    [InlineData("DE", "18", RatingLevel.Adult)]
    [InlineData("GB", "U", RatingLevel.General)]
    [InlineData("GB", "12A", RatingLevel.Teen)]
    [InlineData("AU", "M", RatingLevel.Teen)]
    [InlineData("AU", "MA15+", RatingLevel.Mature)]
    [InlineData("AU", "R18+", RatingLevel.Adult)]
    public void Other_region_ratings_round_up_to_the_shared_scale(string region, string rating, RatingLevel expected)
    {
        Assert.True(RatingScale.TryParse(region, rating, out RatingLevel? level));
        Assert.Equal(expected, level);
    }

    [Fact]
    public void Explicit_block_wins_over_allow()
    {
        ContentPolicy policy = new()
        {
            Allowed = ImmutableHashSet.Create(Movie),
            Blocked = ImmutableHashSet.Create(Movie),
        };

        ContentDecision decision = Evaluate(Title(Movie, ("US", "G")), policy);

        Assert.Equal(new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.ExplicitlyBlocked)), decision);
    }

    [Fact]
    public void Explicit_allow_overrides_rating_and_unrated_policy()
    {
        ContentPolicy policy = new()
        {
            MaxMovieRating = RatingLevel.Preschool,
            Unrated = UnratedPolicy.Block,
            Allowed = ImmutableHashSet.Create(Movie),
        };

        Assert.Equal(ContentReasonCode.ExplicitlyAllowed, Evaluate(Title(Movie, ("US", "R")), policy).Reason.Code);
        Assert.Equal(ContentOutcome.Allowed, Evaluate(Title(Movie), policy).Outcome);
    }

    [Fact]
    public void No_limit_allows_any_rating()
    {
        ContentDecision decision = Evaluate(Title(Show), new ContentPolicy { MaxMovieRating = RatingLevel.General });

        Assert.Equal(new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoRatingLimit)), decision);
    }

    [Theory]
    [InlineData(0, RatingLevel.General)]
    [InlineData(7, RatingLevel.General)]
    [InlineData(8, RatingLevel.ParentalGuidance)]
    [InlineData(12, RatingLevel.ParentalGuidance)]
    [InlineData(13, RatingLevel.Teen)]
    [InlineData(17, RatingLevel.Mature)]
    [InlineData(21, RatingLevel.Adult)]
    public void Profile_age_limits_become_max_ratings(int age, RatingLevel expected)
    {
        ContentPolicy policy = ContentPolicy.FromAgeLimits(age, null);

        Assert.Equal(expected, policy.MaxMovieRating);
        Assert.Null(policy.MaxTvRating);
    }

    [Fact]
    public void Most_restrictive_rule_decides()
    {
        ContentDecision approval = new(ContentOutcome.NeedsApproval, new ContentReason(ContentReasonCode.Unrated));
        ContentPolicyEngine engine = new([new RatingRule(), new FixedRule(approval)]);

        ContentDecision decision = engine.Evaluate(Title(Movie, ("US", "G")), new ContentPolicy { MaxMovieRating = RatingLevel.General });

        Assert.Same(approval, decision);
    }

    [Fact]
    public void Explicit_lists_are_checked_before_rules()
    {
        ContentPolicyEngine engine = new([new FixedRule(new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.AboveRatingLimit)))]);

        ContentDecision decision = engine.Evaluate(Title(Movie), new ContentPolicy { Allowed = ImmutableHashSet.Create(Movie) });

        Assert.Equal(ContentOutcome.Allowed, decision.Outcome);
    }

    [Fact]
    public void Engine_requires_a_rule() => Assert.Throws<ArgumentException>(() => new ContentPolicyEngine([]));

    private static ContentDecision Evaluate(ContentTitle title, ContentPolicy policy) => ContentPolicyEngine.Default.Evaluate(title, policy);

    private static ContentTitle Title(TitleKey key, params (string Region, string Rating)[] certifications) =>
        new(key, [.. certifications.Select(item => new RegionCertification(item.Region, item.Rating))]);

    private sealed class FixedRule(ContentDecision decision) : IContentRule
    {
        public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy) => decision;
    }
}
