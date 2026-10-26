using Wishlistarr.Core.Kids;
using Wishlistarr.Core.Content;

namespace Wishlistarr.Data;

public sealed class KidProfile
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string Avatar { get; set; }

    public string? PinHash { get; set; }

    public int? MovieAgeLimit { get; set; }

    public int? TvAgeLimit { get; set; }

    public required string PlexLabel { get; set; }

    public string? PlexUserId { get; set; }

    public int? WeeklyAllowance { get; set; }

    public bool AutoApprove { get; set; }

    public RatingLevel? AutoApproveMovieRating { get; set; }

    public RatingLevel? AutoApproveTvRating { get; set; }

    public List<int> BlockedGenreIds { get; set; } = [];

    public List<int> BlockedKeywordIds { get; set; } = [];

    public List<string> AllowedOriginalLanguages { get; set; } = [];

    public List<string> HomeRows { get; set; } = [.. HomeRowTypes.Defaults];

    public int FailedPinCount { get; set; }

    public DateTimeOffset? LastFailedPinAt { get; set; }

    public DateTimeOffset? LockedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public bool RemoveOwnedLabelsOnArchive { get; set; }

    public List<KidSession> Sessions { get; } = [];

    public List<KidPinAttempt> PinAttempts { get; } = [];
}
