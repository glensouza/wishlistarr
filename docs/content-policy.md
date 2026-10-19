# Content policy

`ContentPolicyEngine` in `Wishlistarr.Core.Content` decides whether a title is allowed for a kid profile. It is pure: callers fetch certifications (for example with `ITmdbClient.GetCertificationAsync`) and pass them in, and the engine does no I/O.

## Outcomes and reasons

Every decision is a `ContentDecision(Outcome, Reason)`. The outcome is `Allowed`, `NeedsApproval` or `Blocked`. The reason has a `ContentReasonCode` and the data it was based on: the region and rating used, the rating's level and the profile's max level.

| Code | Outcome |
| --- | --- |
| `ExplicitlyBlocked` | Blocked |
| `ExplicitlyAllowed` | Allowed |
| `NoRatingLimit` | Allowed |
| `WithinRatingLimit` | Allowed |
| `AboveRatingLimit` | Blocked |
| `Unrated` | from the unrated policy |

## Order of evaluation

1. A title on the profile's block list is blocked, even if it is also on the allow list.
2. A title on the allow list is allowed, whatever its rating.
3. Otherwise every rule runs and the most restrictive outcome wins. The only rule today is the rating rule; more rules can be added by implementing `IContentRule`.

Titles are identified by `TitleKey(Kind, TmdbId)`. A show is decided at series level: its seasons and episodes share the series key and the series rating.

## Ratings

US ratings map to a shared scale so movies and TV can be compared:

| Level | Movie | TV |
| --- | --- | --- |
| `Preschool` | | TV-Y |
| `Children` | | TV-Y7 |
| `General` | G | TV-G |
| `ParentalGuidance` | PG | TV-PG |
| `Teen` | PG-13 | TV-14 |
| `Mature` | R | TV-MA |
| `Adult` | NC-17 | |

A title is allowed when its level is at or below the profile's max level for its kind. Movies and shows have separate limits; a missing limit means any rating is allowed.

The US certification is used first. If it is missing or not recognized (for example `NR`), the configured region is used, and if that has no usable rating either, the unrated policy applies. Certifications from other regions are ignored. Outside the US, a few letter ratings are recognized (such as `U` in GB and `M` in AU) and ratings containing an age, such as `12A`, `FSK 16` or `MA15+`, are rounded up to the next level, so an imprecise match errs towards stricter.

## Unrated titles

`UnratedPolicy` decides titles without a usable rating: `Allow`, `NeedsApproval` (the default) or `Block`.

## Profile age limits

Profiles store movie and TV age limits. `ContentPolicy.FromAgeLimits` turns them into max levels using each level's minimum age: 0 for `General` and below, 8 for `ParentalGuidance`, 13 for `Teen`, 17 for `Mature` and 18 for `Adult`.
