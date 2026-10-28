using System.Collections.Immutable;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Tests.Content;

public sealed class RatingScalePropertyTests
{
    private static readonly string[] UsRatings = [.. RatingScale.UsMovieRatings, .. RatingScale.UsTvRatings];

    private static readonly Gen<string> UsRating = Gen.Elements(UsRatings);

    private static readonly Gen<RatingLevel> Level = Gen.Elements(Enum.GetValues<RatingLevel>());

    private static readonly Gen<UnratedPolicy> Unrated = Gen.Elements(Enum.GetValues<UnratedPolicy>());

    private static readonly Gen<TitleKey> Key =
        from kind in Gen.Elements(MediaKind.Movie, MediaKind.Show)
        from id in Gen.Choose(1, 1_000_000)
        select new TitleKey(kind, id);

    private static readonly Gen<RegionCertification> Certification =
        from region in Gen.Elements("US", "us", "GB", "DE", "AU", "FR", "JP", "")
        from rating in Gen.OneOf(Gen.Elements(UsRatings), Gen.Elements("NR", "U", "12A", "15", "MA15+", "FSK 16", ""), ArbMap.Default.GeneratorFor<string>().Where(value => value is not null))
        select new RegionCertification(region, rating);

    private static readonly Gen<ContentTitle> Title =
        from key in Key
        from certifications in Gen.ListOf(Certification)
        select new ContentTitle(key, [.. certifications]);

    private static readonly Gen<ContentPolicy> Policy =
        from movie in Gen.OneOf(Gen.Constant<RatingLevel?>(null), Level.Select(level => (RatingLevel?)level))
        from tv in Gen.OneOf(Gen.Constant<RatingLevel?>(null), Level.Select(level => (RatingLevel?)level))
        from unrated in Unrated
        from region in Gen.Elements<string?>(null, "GB", "DE", "us")
        select new ContentPolicy { MaxMovieRating = movie, MaxTvRating = tv, Unrated = unrated, Region = region };

    [Property]
    public Property Each_us_family_is_strictly_increasing_in_rank_order() =>
        Prop.ForAll(
            (from family in Gen.Elements(RatingScale.UsMovieRatings, RatingScale.UsTvRatings)
             from lower in Gen.Choose(0, family.Count - 2)
             from higher in Gen.Choose(lower + 1, family.Count - 1)
             select (family, lower, higher)).ToArbitrary(),
            item => ParseUs(item.family[item.lower]) < ParseUs(item.family[item.higher]));

    [Property]
    public Property Every_us_rating_is_recognized_regardless_of_case_and_padding() =>
        Prop.ForAll(
            (from rating in UsRating
             from lower in ArbMap.Default.GeneratorFor<bool>()
             from pad in Gen.Elements("", " ", "\t", "  ")
             select (rating, text: pad + (lower ? rating.ToLowerInvariant() : rating) + pad)).ToArbitrary(),
            item => RatingScale.TryParse(" us ", item.text, out RatingLevel? level) && level == ParseUs(item.rating));

    [Property]
    public Property A_rating_is_allowed_exactly_when_it_is_not_above_the_max() =>
        Prop.ForAll(
            UsRating.ToArbitrary(),
            Level.ToArbitrary(),
            (rating, max) =>
            {
                ContentDecision decision = Evaluate(MediaKind.Movie, max, ("US", rating));
                return (decision.Outcome == ContentOutcome.Allowed) == (ParseUs(rating) <= max)
                    && decision.Outcome != ContentOutcome.NeedsApproval
                    && decision.Reason.Level == ParseUs(rating)
                    && decision.Reason.MaxLevel == max
                    && decision.Reason.Region == "US";
            });

    [Property]
    public Property Ordering_is_comparable_across_movie_and_tv() =>
        Prop.ForAll(
            Gen.Elements([.. RatingScale.UsMovieRatings]).ToArbitrary(),
            Gen.Elements([.. RatingScale.UsTvRatings]).ToArbitrary(),
            (movie, tv) =>
            {
                RatingLevel movieLevel = ParseUs(movie);
                RatingLevel tvLevel = ParseUs(tv);
                bool tvUnderMovie = Evaluate(MediaKind.Show, movieLevel, ("US", tv)).Outcome == ContentOutcome.Allowed;
                bool movieUnderTv = Evaluate(MediaKind.Movie, tvLevel, ("US", movie)).Outcome == ContentOutcome.Allowed;
                return tvUnderMovie == (tvLevel <= movieLevel) && movieUnderTv == (movieLevel <= tvLevel) && (tvUnderMovie || movieUnderTv);
            });

    [Property]
    public Property Raising_the_max_never_makes_a_decision_stricter() =>
        Prop.ForAll(
            Title.ToArbitrary(),
            Policy.ToArbitrary(),
            Level.ToArbitrary(),
            (title, policy, raised) =>
            {
                RatingLevel? current = policy.MaxRatingFor(title.Key.Kind);
                if (current is null || raised < current)
                {
                    return true;
                }

                ContentPolicy higher = title.Key.Kind == MediaKind.Movie ? policy with { MaxMovieRating = raised } : policy with { MaxTvRating = raised };
                return ContentPolicyEngine.Default.Evaluate(title, higher).Outcome <= ContentPolicyEngine.Default.Evaluate(title, policy).Outcome;
            });

    [Property]
    public Property Explicit_block_always_wins() =>
        Prop.ForAll(
            Title.ToArbitrary(),
            Policy.ToArbitrary(),
            ArbMap.Default.ArbFor<bool>(),
            (title, policy, alsoAllowed) =>
            {
                ImmutableHashSet<TitleKey> allowed = alsoAllowed ? [title.Key] : [];
                ContentPolicy blocking = policy with { Blocked = ImmutableHashSet.Create(title.Key), Allowed = allowed };
                ContentDecision decision = ContentPolicyEngine.Default.Evaluate(title, blocking);
                return decision.Outcome == ContentOutcome.Blocked && decision.Reason.Code == ContentReasonCode.ExplicitlyBlocked;
            });

    [Property]
    public Property Unrecognized_or_missing_ratings_follow_the_unrated_policy() =>
        Prop.ForAll(
            Gen.ListOf(ArbMap.Default.GeneratorFor<string>().Where(value => value is not null && !RatingScale.TryParse("US", value, out _))).ToArbitrary(),
            Level.ToArbitrary(),
            Unrated.ToArbitrary(),
            (ratings, max, unrated) =>
            {
                ContentTitle title = new(new TitleKey(MediaKind.Show, 1), [.. ratings.Select(rating => new RegionCertification("US", rating))]);
                ContentDecision decision = ContentPolicyEngine.Default.Evaluate(title, new ContentPolicy { MaxTvRating = max, Unrated = unrated });
                return decision.Outcome == ExpectedUnrated(unrated) && decision.Reason.Code == ContentReasonCode.Unrated;
            });

    [Property]
    public Property Tv_y7_is_allowed_exactly_from_age_seven() =>
        Prop.ForAll(
            Gen.Choose(0, 120).ToArbitrary(),
            age =>
            {
                ContentTitle title = new(new TitleKey(MediaKind.Show, 1), [new RegionCertification("US", "TV-Y7")]);
                ContentDecision decision = ContentPolicyEngine.Default.Evaluate(title, ContentPolicy.FromAgeLimits(null, age));
                return (decision.Outcome == ContentOutcome.Allowed) == (age >= 7);
            });

    [Property]
    public Property Every_decision_has_a_reason_consistent_with_its_outcome() =>
        Prop.ForAll(
            Title.ToArbitrary(),
            Policy.ToArbitrary(),
            (title, policy) =>
            {
                ContentDecision decision = ContentPolicyEngine.Default.Evaluate(title, policy);
                return decision.Reason.Code switch
                {
                    ContentReasonCode.WithinRatingLimit or ContentReasonCode.NoRatingLimit or ContentReasonCode.ExplicitlyAllowed
                        or ContentReasonCode.NoBlockedGenre or ContentReasonCode.NoBlockedKeyword
                        or ContentReasonCode.NoLanguageLimit or ContentReasonCode.LanguageAllowed
                        or ContentReasonCode.NoBlockedDescriptor or ContentReasonCode.NoBlockedTopic => decision.Outcome == ContentOutcome.Allowed,
                    ContentReasonCode.UnknownGenres => decision.Outcome == ContentOutcome.NeedsApproval,
                    ContentReasonCode.AboveRatingLimit => decision.Outcome == ContentOutcome.Blocked && decision.Reason.Level > decision.Reason.MaxLevel,
                    ContentReasonCode.Unrated => decision.Outcome == ExpectedUnrated(policy.Unrated) && decision.Reason.MaxLevel == policy.MaxRatingFor(title.Key.Kind),
                    ContentReasonCode.ExplicitlyBlocked or ContentReasonCode.BlockedGenre or ContentReasonCode.BlockedKeyword or ContentReasonCode.LanguageNotAllowed
                        or ContentReasonCode.BlockedDescriptor or ContentReasonCode.BlockedTopic =>
                        decision.Outcome == ContentOutcome.Blocked,
                    _ => false,
                };
            });

    [Property]
    public Property Max_level_for_age_never_exceeds_the_age_and_grows_with_it() =>
        Prop.ForAll(
            Gen.Choose(0, 120).ToArbitrary(),
            Gen.Choose(0, 120).ToArbitrary(),
            (a, b) =>
            {
                (int younger, int older) = a <= b ? (a, b) : (b, a);
                return RatingScale.MinimumAge(RatingScale.MaxLevelForAge(younger)) <= younger
                    && RatingScale.MaxLevelForAge(younger) <= RatingScale.MaxLevelForAge(older);
            });

    [Property]
    public Property Region_ages_never_round_down() =>
        Prop.ForAll(
            Gen.Choose(0, 99).ToArbitrary(),
            Gen.Choose(0, 99).ToArbitrary(),
            (a, b) =>
            {
                (int younger, int older) = a <= b ? (a, b) : (b, a);
                RatingLevel level = RatingScale.LevelForMinimumAge(younger);
                return (younger > RatingScale.MinimumAge(RatingLevel.Adult) || RatingScale.MinimumAge(level) >= younger)
                    && level <= RatingScale.LevelForMinimumAge(older);
            });

    private static ContentOutcome ExpectedUnrated(UnratedPolicy unrated) => unrated switch
    {
        UnratedPolicy.Allow => ContentOutcome.Allowed,
        UnratedPolicy.Block => ContentOutcome.Blocked,
        _ => ContentOutcome.NeedsApproval,
    };

    private static RatingLevel ParseUs(string rating) =>
        RatingScale.TryParse("US", rating, out RatingLevel? level) ? level.Value : throw new ArgumentException(rating);

    private static ContentDecision Evaluate(MediaKind kind, RatingLevel max, params (string Region, string Rating)[] certifications)
    {
        ContentTitle title = new(new TitleKey(kind, 1), [.. certifications.Select(item => new RegionCertification(item.Region, item.Rating))]);
        ContentPolicy policy = kind == MediaKind.Movie ? new ContentPolicy { MaxMovieRating = max } : new ContentPolicy { MaxTvRating = max };
        return ContentPolicyEngine.Default.Evaluate(title, policy);
    }
}
