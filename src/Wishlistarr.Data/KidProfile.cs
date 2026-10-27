using Wishlistarr.Core.Content;
using Wishlistarr.Core.Kids;
using Wishlistarr.Core.Requests;

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

    public List<ProfileTitleRule> TitleRules { get; set; } = [];

    private bool contentRulesLoaded;

    internal void MarkContentRulesLoaded() => this.contentRulesLoaded = true;

    public ContentPolicy ContentPolicy()
    {
        if (!this.contentRulesLoaded) throw new InvalidOperationException("Load the profile with FirstWithContentRulesAsync before building its content policy.");
        return Wishlistarr.Core.Content.ContentPolicy.ForProfile(
        this.MovieAgeLimit, this.TvAgeLimit, this.BlockedGenreIds, this.BlockedKeywordIds, this.AllowedOriginalLanguages) with
        {
            Blocked = this.TitleRules.Where(row => row.Blocked).Select(row => new TitleKey(row.Kind, row.TmdbId)).ToHashSet(),
            Allowed = this.TitleRules.Where(row => !row.Blocked).Select(row => new TitleKey(row.Kind, row.TmdbId)).ToHashSet(),
        };
    }

    public AutoApproveRule AutoApproval() => new(this.AutoApprove, this.AutoApproveMovieRating, this.AutoApproveTvRating);

    public int FailedPinCount { get; set; }

    public DateTimeOffset? LastFailedPinAt { get; set; }

    public DateTimeOffset? LockedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public bool RemoveOwnedLabelsOnArchive { get; set; }

    public List<KidSession> Sessions { get; } = [];

    public List<KidPinAttempt> PinAttempts { get; } = [];
}
