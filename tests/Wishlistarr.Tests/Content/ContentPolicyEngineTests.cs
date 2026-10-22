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
    [InlineData("TV-G", RatingLevel.General)]
    [InlineData("TV-Y7", RatingLevel.Children)]
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
        Assert.True(RatingScale.TryParse("US", movie, out RatingLevel? movieLevel));
        Assert.True(RatingScale.TryParse("US", tv, out RatingLevel? tvLevel));
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
    [InlineData("DE", "6", RatingLevel.Children)]
    [InlineData("DE", "12", RatingLevel.Teen)]
    [InlineData("DE", "16", RatingLevel.Mature)]
    [InlineData("DE", "18", RatingLevel.Adult)]
    [InlineData("GB", "U", RatingLevel.General)]
    [InlineData("GB", "12A", RatingLevel.Teen)]
    [InlineData("AU", "M", RatingLevel.Mature)]
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
    public void No_limit_allows_any_recognized_rating()
    {
        ContentDecision decision = Evaluate(Title(Show, ("US", "TV-MA")), new ContentPolicy { MaxMovieRating = RatingLevel.General });

        Assert.Equal(
            new ContentDecision(ContentOutcome.Allowed, new ContentReason(ContentReasonCode.NoRatingLimit, "US", "TV-MA", RatingLevel.Mature)),
            decision);
    }

    [Theory]
    [InlineData(UnratedPolicy.Allow, ContentOutcome.Allowed)]
    [InlineData(UnratedPolicy.NeedsApproval, ContentOutcome.NeedsApproval)]
    [InlineData(UnratedPolicy.Block, ContentOutcome.Blocked)]
    public void No_limit_still_applies_the_unrated_policy(UnratedPolicy unrated, ContentOutcome expected)
    {
        ContentPolicy policy = ContentPolicy.FromAgeLimits(null, null) with { Unrated = unrated };

        ContentDecision decision = Evaluate(Title(Show, ("US", "NR")), policy);

        Assert.Equal(new ContentDecision(expected, new ContentReason(ContentReasonCode.Unrated, "US", "NR")), decision);
    }

    [Fact]
    public void Strictest_rating_within_a_region_is_used()
    {
        ContentDecision decision = Evaluate(
            Title(Movie, ("US", "PG"), ("US", "NR"), ("US", "PG-13"), ("US", "G")),
            new ContentPolicy { MaxMovieRating = RatingLevel.ParentalGuidance });

        Assert.Equal(
            new ContentReason(ContentReasonCode.AboveRatingLimit, "US", "PG-13", RatingLevel.Teen, RatingLevel.ParentalGuidance),
            decision.Reason);
    }

    [Fact]
    public void Missing_region_or_rating_is_treated_as_unrated()
    {
        ContentTitle title = new(Movie, [new RegionCertification(null!, "PG"), new RegionCertification("US", null!), null!]);

        ContentDecision decision = Evaluate(title, new ContentPolicy { MaxMovieRating = RatingLevel.Mature });

        Assert.Equal(
            new ContentDecision(ContentOutcome.NeedsApproval, new ContentReason(ContentReasonCode.Unrated, "US", null, MaxLevel: RatingLevel.Mature)),
            decision);
        Assert.Equal(ContentOutcome.NeedsApproval, Evaluate(new ContentTitle(Movie, null!), new ContentPolicy()).Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void Young_age_limits_exclude_tv_y7(int age)
    {
        ContentPolicy policy = ContentPolicy.FromAgeLimits(age, age);

        Assert.Equal(ContentOutcome.Blocked, Evaluate(Title(Show, ("US", "TV-Y7")), policy).Outcome);
        Assert.Equal(ContentOutcome.Allowed, Evaluate(Title(Show, ("US", "TV-G")), policy).Outcome);
        Assert.Equal(ContentOutcome.Allowed, Evaluate(Title(Movie, ("US", "G")), policy).Outcome);
    }

    [Fact]
    public void Age_limit_of_seven_allows_tv_y7() =>
        Assert.Equal(ContentOutcome.Allowed, Evaluate(Title(Show, ("US", "TV-Y7")), ContentPolicy.FromAgeLimits(null, 7)).Outcome);

    [Theory]
    [InlineData(0, RatingLevel.General)]
    [InlineData(6, RatingLevel.General)]
    [InlineData(7, RatingLevel.Children)]
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

    [Fact]
    public void Each_rule_type_is_configurable_on_the_policy()
    {
        ContentTitle title = MovieTitle(genres: [28], keywords: [1721], language: "ja");
        ContentPolicy open = ContentPolicy.ForProfile(null, null, null, null, null);
        ContentPolicy genres = ContentPolicy.ForProfile(null, null, [28], null, null);
        ContentPolicy keywords = ContentPolicy.ForProfile(null, null, null, [1721], null);
        ContentPolicy languages = ContentPolicy.ForProfile(null, null, null, null, ["EN"]);

        Assert.Equal(ContentReasonCode.NoRatingLimit, Evaluate(title, open).Reason.Code);
        Assert.Equal(new ContentReason(ContentReasonCode.BlockedGenre, GenreId: 28), Evaluate(title, genres).Reason);
        Assert.Equal(new ContentReason(ContentReasonCode.BlockedKeyword, KeywordId: 1721), Evaluate(title, keywords).Reason);
        Assert.Equal(new ContentReason(ContentReasonCode.LanguageNotAllowed, OriginalLanguage: "ja"), Evaluate(title, languages).Reason);
    }

    [Fact]
    public void Explicit_allow_loses_to_explicit_block_when_other_rules_would_also_block()
    {
        ContentPolicy policy = new()
        {
            Allowed = ImmutableHashSet.Create(Movie),
            Blocked = ImmutableHashSet.Create(Movie),
            BlockedGenreIds = ImmutableHashSet.Create(28),
            BlockedKeywordIds = ImmutableHashSet.Create(1721),
            AllowedOriginalLanguages = ImmutableHashSet.Create("en"),
        };

        ContentDecision decision = Evaluate(MovieTitle(genres: [28], keywords: [1721], language: "ja"), policy);

        Assert.Equal(new ContentDecision(ContentOutcome.Blocked, new ContentReason(ContentReasonCode.ExplicitlyBlocked)), decision);
    }

    [Fact]
    public void Explicit_allow_beats_genre_keyword_and_language_blocks()
    {
        ContentPolicy policy = new()
        {
            Allowed = ImmutableHashSet.Create(Movie),
            BlockedGenreIds = ImmutableHashSet.Create(28),
            BlockedKeywordIds = ImmutableHashSet.Create(1721),
            AllowedOriginalLanguages = ImmutableHashSet.Create("en"),
        };

        ContentDecision decision = Evaluate(MovieTitle(rating: "R", genres: [28], keywords: [1721], language: "ja"), policy);

        Assert.Equal(ContentReasonCode.ExplicitlyAllowed, decision.Reason.Code);
        Assert.Equal(ContentOutcome.Allowed, decision.Outcome);
    }

    [Fact]
    public void A_blocked_genre_names_the_genre_and_wins_over_an_allowed_rating()
    {
        ContentPolicy policy = new()
        {
            MaxMovieRating = RatingLevel.ParentalGuidance,
            BlockedGenreIds = ImmutableHashSet.Create(53, 28),
        };

        ContentDecision decision = Evaluate(MovieTitle(rating: "PG", genres: [12, 28, 53]), policy);

        Assert.Equal(ContentOutcome.Blocked, decision.Outcome);
        Assert.Equal(new ContentReason(ContentReasonCode.BlockedGenre, GenreId: 28), decision.Reason);
    }

    [Fact]
    public void A_rating_block_is_kept_when_the_genre_is_blocked_too()
    {
        ContentPolicy policy = new()
        {
            MaxMovieRating = RatingLevel.ParentalGuidance,
            BlockedGenreIds = ImmutableHashSet.Create(28),
        };

        ContentDecision decision = Evaluate(MovieTitle(rating: "PG-13", genres: [28]), policy);

        Assert.Equal(ContentReasonCode.AboveRatingLimit, decision.Reason.Code);
        Assert.Equal(ContentOutcome.Blocked, decision.Outcome);
    }

    [Fact]
    public void A_blocked_genre_wins_over_an_unrated_title()
    {
        ContentPolicy policy = new() { BlockedGenreIds = ImmutableHashSet.Create(27) };

        ContentDecision decision = Evaluate(MovieTitle(rating: null, genres: [27]), policy);

        Assert.Equal(new ContentReason(ContentReasonCode.BlockedGenre, GenreId: 27), decision.Reason);
    }

    [Fact]
    public void A_blocked_keyword_wins_over_a_disallowed_language()
    {
        ContentTitle title = MovieTitle(keywords: [9, 4], language: "ja");
        ContentPolicy both = new()
        {
            BlockedKeywordIds = ImmutableHashSet.Create(4),
            AllowedOriginalLanguages = ImmutableHashSet.Create("en"),
        };
        ContentPolicy languageOnly = new() { AllowedOriginalLanguages = ImmutableHashSet.Create("en") };
        ContentPolicy keywordOnly = new() { BlockedKeywordIds = ImmutableHashSet.Create(4), AllowedOriginalLanguages = ImmutableHashSet.Create("ja") };

        Assert.Equal(new ContentReason(ContentReasonCode.BlockedKeyword, KeywordId: 4), Evaluate(title, both).Reason);
        Assert.Equal(new ContentReason(ContentReasonCode.LanguageNotAllowed, OriginalLanguage: "ja"), Evaluate(title, languageOnly).Reason);
        Assert.Equal(new ContentReason(ContentReasonCode.BlockedKeyword, KeywordId: 4), Evaluate(title, keywordOnly).Reason);
    }

    [Fact]
    public void Languages_set_directly_on_a_policy_are_canonicalised()
    {
        ContentPolicy policy = new() { AllowedOriginalLanguages = ImmutableHashSet.Create(" EN ", "x") };

        Assert.Equal(["en"], policy.AllowedOriginalLanguages);
        Assert.Equal(ContentOutcome.Allowed, Evaluate(MovieTitle(rating: "PG", language: "en"), policy with { MaxMovieRating = RatingLevel.Teen }).Outcome);
    }

    [Fact]
    public void A_language_on_the_allow_list_does_not_block()
    {
        ContentPolicy policy = ContentPolicy.ForProfile(8, null, null, null, ["EN"]);

        ContentDecision decision = Evaluate(MovieTitle(rating: "PG", language: "en"), policy);

        Assert.Equal(ContentOutcome.Allowed, decision.Outcome);
        Assert.Equal(ContentReasonCode.WithinRatingLimit, decision.Reason.Code);
        Assert.Equal(
            new ContentReason(ContentReasonCode.LanguageAllowed, OriginalLanguage: "en"),
            new OriginalLanguageRule().Evaluate(MovieTitle(language: "en"), policy).Reason);
    }

    [Fact]
    public void An_empty_language_list_does_not_restrict()
    {
        ContentDecision decision = new OriginalLanguageRule().Evaluate(MovieTitle(language: "ja"), new ContentPolicy());

        Assert.Equal(new ContentReason(ContentReasonCode.NoLanguageLimit), decision.Reason);
        Assert.Equal(ContentOutcome.Allowed, decision.Outcome);
    }

    [Fact]
    public void Missing_genres_need_approval_when_genres_are_blocked_and_missing_keywords_do_not_match()
    {
        ContentPolicy blocking = new()
        {
            BlockedGenreIds = ImmutableHashSet.Create(28),
            BlockedKeywordIds = ImmutableHashSet.Create(9),
        };
        ContentPolicy open = new();

        ContentDecision decision = Evaluate(MovieTitle(), blocking);

        Assert.Equal(ContentOutcome.NeedsApproval, decision.Outcome);
        Assert.Equal(ContentReasonCode.UnknownGenres, decision.Reason.Code);
        Assert.Equal(ContentReasonCode.UnknownGenres, new GenreBlockRule().Evaluate(MovieTitle(genres: []), blocking).Reason.Code);
        Assert.Equal(ContentReasonCode.NoBlockedGenre, new GenreBlockRule().Evaluate(MovieTitle(genres: [12]), blocking).Reason.Code);
        Assert.Equal(ContentReasonCode.NoBlockedGenre, new GenreBlockRule().Evaluate(MovieTitle(), open).Reason.Code);
        Assert.Equal(ContentReasonCode.NoBlockedKeyword, new KeywordBlockRule().Evaluate(MovieTitle(keywords: []), blocking).Reason.Code);
        Assert.Equal(ContentReasonCode.NoBlockedKeyword, new KeywordBlockRule().Evaluate(MovieTitle(), blocking).Reason.Code);
    }

    [Fact]
    public void A_missing_language_is_blocked_only_when_the_allow_list_is_set()
    {
        ContentTitle missing = MovieTitle(language: "  ");
        ContentTitle unrecognized = MovieTitle(language: "en-US");
        ContentPolicy open = new();
        ContentPolicy restricted = new() { AllowedOriginalLanguages = ImmutableHashSet.Create("en") };

        Assert.Equal(ContentReasonCode.NoRatingLimit, Evaluate(missing, open).Reason.Code);
        Assert.Equal(new ContentReason(ContentReasonCode.LanguageNotAllowed), Evaluate(missing, restricted).Reason);
        Assert.Equal(new ContentReason(ContentReasonCode.LanguageNotAllowed), Evaluate(unrecognized, restricted).Reason);
    }

    private static ContentDecision Evaluate(ContentTitle title, ContentPolicy policy) => ContentPolicyEngine.Default.Evaluate(title, policy);

    private static ContentTitle MovieTitle(string? rating = "G", int[]? genres = null, int[]? keywords = null, string? language = null)
    {
        RegionCertification[] certifications = rating is null ? [] : [new RegionCertification("US", rating)];
        return new ContentTitle(Movie, certifications, genres, keywords, language);
    }

    private static ContentTitle Title(TitleKey key, params (string Region, string Rating)[] certifications) =>
        new(key, [.. certifications.Select(item => new RegionCertification(item.Region, item.Rating))]);

    private sealed class FixedRule(ContentDecision decision) : IContentRule
    {
        public ContentDecision Evaluate(ContentTitle title, ContentPolicy policy) => decision;
    }
}
