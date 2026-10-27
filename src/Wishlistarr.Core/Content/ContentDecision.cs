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
    BlockedGenre,
    NoBlockedGenre,
    UnknownGenres,
    BlockedKeyword,
    NoBlockedKeyword,
    NoLanguageLimit,
    LanguageAllowed,
    LanguageNotAllowed,
    PreviouslyRevoked,
    MetadataUnavailable,
}

public sealed record ContentReason(
    ContentReasonCode Code,
    string? Region = null,
    string? Rating = null,
    RatingLevel? Level = null,
    RatingLevel? MaxLevel = null,
    int? GenreId = null,
    int? KeywordId = null,
    string? OriginalLanguage = null);

public sealed record ContentDecision(ContentOutcome Outcome, ContentReason Reason);
