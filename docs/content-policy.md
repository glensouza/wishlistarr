# Content policy

`ContentPolicyEngine` in `Wishlistarr.Core.Content` decides whether a title is allowed for a kid profile. It is pure: callers pass certifications, TMDB genre ids, TMDB keyword ids and the original language, and the engine does no I/O.

## Outcomes and reasons

Every decision is a `ContentDecision(Outcome, Reason)`. The outcome is `Allowed`, `NeedsApproval` or `Blocked`. The reason has a `ContentReasonCode` and the data it was based on.

| Code | Outcome | Data |
| --- | --- | --- |
| `ExplicitlyBlocked` | Blocked | |
| `ExplicitlyAllowed` | Allowed | |
| `NoRatingLimit` | Allowed | region, rating, level |
| `WithinRatingLimit` | Allowed | region, rating, level, max level |
| `AboveRatingLimit` | Blocked | region, rating, level, max level |
| `Unrated` | from the unrated policy | region, rating, max level |
| `BlockedGenre` | Blocked | genre id |
| `NoBlockedGenre` | Allowed | |
| `UnknownGenres` | NeedsApproval | |
| `BlockedKeyword` | Blocked | keyword id |
| `NoBlockedKeyword` | Allowed | |
| `NoLanguageLimit` | Allowed | |
| `LanguageAllowed` | Allowed | original language |
| `LanguageNotAllowed` | Blocked | original language, omitted when it is missing |

The genre id and keyword id are the TMDB ids. The original language is the ISO 639-1 code.

## Order of evaluation

1. A title on the profile's block list is blocked, even if it is also on the allow list. This is checked before any rule, so an explicit block wins over a genre, keyword or language result too.
2. A title on the allow list is allowed, whatever its rating, genres, keywords or original language.
3. Otherwise every rule runs and the most restrictive outcome wins. `Allowed` is less restrictive than `NeedsApproval`, which is less restrictive than `Blocked`. When two rules return the same outcome, the earlier rule's reason is kept.

The rules, in order, are the rating rule, the genre block, the keyword block and the original-language allow list. Each implements `IContentRule`.

Titles are identified by `TitleKey(Kind, TmdbId)`. A show is decided at series level: its seasons and episodes share the series key and the series rating.

## Genres

A profile can block TMDB genre ids, the same integers `TmdbGenre.Id` carries (for example `28` for Action). A title is blocked when any of its genre ids is on that list. The reason is `BlockedGenre` and the id is the first blocked id in the title's own list.

When the profile blocks at least one genre and the title has no genre ids, the rule cannot tell whether a blocked genre is present. Unknown does not count as allowed, so the decision is `UnknownGenres` and the outcome is `NeedsApproval`. An empty list and a missing list are the same. An empty block list blocks nothing, and the decision is then `NoBlockedGenre` even when the title has no genres. `NoBlockedGenre` and `UnknownGenres` become the engine's decision only when no earlier rule is equally or more restrictive.

## Keywords

Keyword blocks use TMDB keyword ids. A movie's list comes from `GET /movie/{id}/keywords` (`keywords`) and a show's from `GET /tv/{id}/keywords` (`results`); each entry is an `id` and a `name`. A title is blocked when any of its keyword ids is on the profile's list. The reason is `BlockedKeyword` and the id is the first blocked id in the title's own list.

The engine does not call TMDB. Callers pass the ids from a lookup that succeeded. An empty list is not a match, and the rule's decision is `NoBlockedKeyword`. An empty block list blocks nothing. When the profile has keyword blocks, [search](kid-search.md) and [requests](requests.md) do that lookup and refuse the title if it fails or times out, instead of passing an empty list in place of a failure.

## Original language

A profile can list the original languages it allows. Codes are ISO 639-1, the two-letter `original_language` value TMDB returns (`en`, `ja`, `zh`). TMDB uses `cn` for Cantonese and `xx` for no language, so `zh` does not cover Cantonese. `xx` is a language code like any other: a title marked with it is not treated as missing a language. Comparison is case-insensitive. Profiles store the codes in lowercase, and the rule uses that set. The reason reports the code in lowercase. A value that is not two ASCII letters is not a language code: `en-US` is ignored, not treated as `en`.

An empty allow list means no language restriction (`NoLanguageLimit`). That includes a title with no language.

When the list is not empty, the title's original language must be on it (`LanguageAllowed`). Any other recognized language is blocked (`LanguageNotAllowed`) and the reason carries that language. A missing language, or a value that is not an ISO 639-1 code, is blocked too, and the reason carries no language. The list says which languages are allowed, and a title with no language cannot be shown to be one of them.

## Ratings

US ratings map to a shared scale so movies and TV can be compared:

| Level | Movie | TV | Minimum age |
| --- | --- | --- | --- |
| `Preschool` | | TV-Y | 0 |
| `General` | G | TV-G | 0 |
| `Children` | | TV-Y7 | 7 |
| `ParentalGuidance` | PG | TV-PG | 8 |
| `Teen` | PG-13 | TV-14 | 13 |
| `Mature` | R | TV-MA | 17 |
| `Adult` | NC-17 | | 18 |

TV-Y7 ranks above G and TV-G because it is meant for children aged 7 and up, while G and TV-G suit all ages.

A title is allowed when its level is at or below the profile's max level for its kind. Movies and shows have separate limits. A missing limit means any recognized rating is allowed; titles without a usable rating still follow the unrated policy.

The US certification is used first. If it is missing or not recognized (for example `NR`), the configured region is used, and if that has no usable rating either, the unrated policy applies. Certifications from other regions are ignored. When a region has several ratings, the strictest recognized one is used. Missing regions or ratings count as no rating.

Outside the US, a few letter ratings are recognized (`U` in GB and FR, `G` and `PG` in AU and CA, `PG` in GB, and `M` in AU, which is treated as `Mature` because it is recommended for 15 and up). Ratings containing an age, such as `12A`, `FSK 16` or `MA15+`, are rounded up to the next level, so an imprecise match errs towards stricter.

## Unrated titles

`UnratedPolicy` decides titles without a usable rating: `Allow`, `NeedsApproval` (the default) or `Block`. It applies whether or not the profile has a rating limit.

## Profile configuration

Profiles store movie and TV age limits, blocked genre ids, blocked keyword ids and allowed original languages. `ContentPolicy.FromAgeLimits` turns each age limit into the highest level whose minimum age is at or below it: ages 0 to 6 allow up to `General`, 7 allows TV-Y7, 8 to 12 allow PG and TV-PG, and so on. `ContentPolicy.ForProfile` does the same and copies the three lists. See [kid profiles](kid-profiles.md) for how they are saved.

`Preschool` cannot be reached from an age limit. TV-Y, G and TV-G are all suitable for every age, so no age separates them; a policy can still set `Preschool` as its max level directly to allow only TV-Y.
