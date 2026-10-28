# Kid home

`GET /api/v1/kid/home` needs a Kid session. It always answers for the signed-in profile; there is no profile id to pass. The response is `{ rows }`. Each row has `id`, `genreId` and `genreName` (both null except on a genre shelf; the name is TMDB's English name, or null when TMDB's genre list could not be read) and `items`. An item is the same shape search returns: `kind`, `tmdbId`, `title`, `year`, `overview`, `posterUrl`, `availability` and `needsParent`. Empty rows are left out. Responses are sent with `Cache-Control: private, no-store`.

Home uses the same content pipeline as [search](kid-search.md): the profile's content policy, US certification lookups, keyword lookups when the profile blocks keywords, and DoesTheDogDie lookups only when a key is saved and the profile blocks topics, at most 8 lookups at a time. Gathering the rows has a 3 second budget and the certification pass that follows has its own 3 seconds (see Limits). A title is returned only when the decision is `Allowed`. Anything blocked, needing approval, unrated under the default policy, or whose certification, keywords or required content warnings could not be read, is left out entirely. `needsParent` is therefore false on every home item. Search still returns masked titles that need a parent; home does not. Home items do not include topic names, the attribution link or a parent note.

## Rows

Parents choose the rows on the profile (`homeRows` on the [profile API](kid-profiles.md)). The server expands them in that order:

| Stored value | Shelf |
| --- | --- |
| `popular` | Popular for my age. One movie discover and one TV discover, ordered by `popularity.desc`. |
| `library` | New in our library. The newest distinct TMDB titles in the [library index](library.md), by the time Plex added them. Items indexed before the library index stored that time have none until the next full refresh and are listed after the others. |
| `genre` | Three shelves, Animation (`16`), Family (`10751`) and Comedy (`35`), skipping any id the profile blocks. |
| `genre:<id>` | One shelf for that TMDB genre id. A blocked id is skipped and not requested. A genre that exists only for movies or only for TV is discovered for that type alone; an id TMDB does not list is tried for both. |

`genre:016` is stored as `genre:16`. Duplicate tokens are stored once. The `genre` token counts as one of the ten stored values; the three shelves it expands into do not. An empty list stores no rows and the home answer is `{ "rows": [] }` without calling TMDB.

Movie discover passes `certification_country=US` and `certification.lte` when the profile has a movie age limit. The ceiling is the US movie certification for that age: G through age 7 (movies have no TV-Y7 equivalent), PG for 8 to 12, PG-13 for 13 to 16, R for 17 and NC-17 from 18. With no movie age limit those parameters are omitted. [Discover TV](https://developer.themoviedb.org/reference/discover-tv) has no certification filter, so shows are ordered by popularity and then kept or dropped by the policy. `certification.lte` includes unrated movies (`NR` sorts below G); the policy still drops them. Discover responses do not carry `media_type`, so home treats each call as the kind it asked for. Every call sends `include_adult=false` and asks for page 1 only.

A title already in the library index has `availability: "inLibrary"`. The kid UI shows a badge whose name includes the title. When the index cannot answer, `availability` is null and there is no badge. Posters are `posterUrl` values on `/api/v1/kid/images/tmdb/w342/...`. The browser never loads `image.tmdb.org`. A missing poster is null, and the page keeps a 2:3 frame that screen readers skip; the title under it names the card either way.

## Limits

One home load makes at most 8 discover calls, 4 at a time, and keeps at most 8 titles from each row. That is 4 discover rows plus the library row, so at most 40 distinct titles share one certification pass. Rows past the discover limit are left out. The whole gathering phase (discover calls, genre names and library details) has one 3 second budget; the certification pass that follows has its own 3 seconds. Rows that have not finished when the budget runs out are left out, never shown unchecked, and home answers with the rows that did finish. Library details are not stored. Discover is cached for 1 hour, details, keywords and genre lists for 12 hours, and certifications for 7 days, the same as the TMDB client. A second load of the same rows does not repeat those calls. Failed responses are not cached.

If a discover call or a library detail fails, that source is left out. Home answers `200` when at least one TMDB call succeeded, including an empty page. It answers `502` when every discover or library detail call it made failed. A missing API key answers `503`. Both say `Home isn't working right now.` and name no title. A title whose certification or keywords cannot be read is left out, and so is a title whose content warnings could not be read when the profile blocks topics. When every certification lookup fails home answers `200` with no rows. The same happens when the budget runs out before any row finishes. No rows configured, or a library index that is not ready yet, answers `200` with no rows and does not call TMDB.

Without a Kid session home answers `401`, and `403` for a parent session. Archiving a profile ends its Kid sessions, so the next request is `401` and TMDB is not called. `403` is also what home answers if the profile cannot be found by the time home reads it.

"Because you watched" is not a home row.
