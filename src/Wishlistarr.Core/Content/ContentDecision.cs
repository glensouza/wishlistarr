namespace Wishlistarr.Core.Content;

public enum ContentOutcome
{
    Allowed,
    NeedsApproval,
    Blocked,
}

public enum ContentReasonCode
{
    ExplicitlyBlocked,
    ExplicitlyAllowed,
    NoRatingLimit,
    WithinRatingLimit,
    AboveRatingLimit,
    Unrated,
}

public sealed record ContentReason(
    ContentReasonCode Code,
    string? Region = null,
    string? Rating = null,
    RatingLevel? Level = null,
    RatingLevel? MaxLevel = null);

public sealed record ContentDecision(ContentOutcome Outcome, ContentReason Reason);
