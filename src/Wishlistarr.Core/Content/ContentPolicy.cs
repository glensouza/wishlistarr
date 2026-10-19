using System.Collections.Immutable;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Content;

public enum UnratedPolicy
{
    Allow,
    NeedsApproval,
    Block,
}

// A show is one title: seasons and episodes share the series key and the series certification.
public sealed record TitleKey(MediaKind Kind, int TmdbId);

public sealed record RegionCertification(string Region, string Rating);

public sealed record ContentTitle(TitleKey Key, IReadOnlyList<RegionCertification> Certifications);

public sealed record ContentPolicy
{
    public RatingLevel? MaxMovieRating { get; init; }

    public RatingLevel? MaxTvRating { get; init; }

    public UnratedPolicy Unrated { get; init; } = UnratedPolicy.NeedsApproval;

    public string? Region { get; init; }

    public IReadOnlySet<TitleKey> Allowed { get; init; } = ImmutableHashSet<TitleKey>.Empty;

    public IReadOnlySet<TitleKey> Blocked { get; init; } = ImmutableHashSet<TitleKey>.Empty;

    public RatingLevel? MaxRatingFor(MediaKind kind) => kind == MediaKind.Movie ? this.MaxMovieRating : this.MaxTvRating;

    public static ContentPolicy FromAgeLimits(int? movieAgeLimit, int? tvAgeLimit) => new()
    {
        MaxMovieRating = movieAgeLimit is int movieAge ? RatingScale.MaxLevelForAge(movieAge) : null,
        MaxTvRating = tvAgeLimit is int tvAge ? RatingScale.MaxLevelForAge(tvAge) : null,
    };
}
