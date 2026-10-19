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

## Profile age limits

Profiles store movie and TV age limits. `ContentPolicy.FromAgeLimits` turns each limit into the highest level whose minimum age is at or below it: ages 0 to 6 allow up to `General`, 7 allows TV-Y7, 8 to 12 allow PG and TV-PG, and so on.

`Preschool` cannot be reached from an age limit. TV-Y, G and TV-G are all suitable for every age, so no age separates them; a policy can still set `Preschool` as its max level directly to allow only TV-Y.
