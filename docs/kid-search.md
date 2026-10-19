# Kid search

Kids look for movies and shows with search as you type. `GET /api/v1/kid/search?q=<text>&page=<n>` needs a Kid session and always answers for the signed-in profile; there is no way to ask for another profile. Posters are served to kids from `GET /api/v1/kid/images/tmdb/{size}/{file}`, which is the same proxy as the parent one (see [TMDB](tmdb.md#image-proxy)).

## Query rules

- The query is trimmed and runs of whitespace are collapsed. It must be 2 to 100 characters and contain no control characters, otherwise the answer is `400` and TMDB is not called.
- `page` is 1 to 10 and defaults to 1. Each page is one page of TMDB results (up to 20), so a page can hold fewer titles than that after filtering.
- Only movies and shows are returned; people and other result types are dropped.

## Response

`{ query, page, hasMore, results }`. Each result has `kind`, `tmdbId`, `title`, `year`, `overview`, `posterUrl` and `availability`. There are no totals and no counts of hidden titles. `hasMore` comes from TMDB's own page count, so it says nothing about what was filtered out. Responses are sent with `Cache-Control: private, no-store`.

`availability` is `inLibrary` or `notInLibrary` when a library index can answer, and `null` when there is no index or it has not synced yet. Search never asks Plex directly.

Failures answer `502` (TMDB unreachable) or `503` (no API key) with a message that is safe to show a kid.

## Filtering

Every result is checked with the same content policy and US certification lookup as [requests](requests.md#asking-for-a-title), using the profile's age limits.

- `Blocked` titles are removed before anything is returned.
- Titles whose certification cannot be read, because TMDB failed for that title or the lookup did not finish within the budget below, are removed. Unknown never counts as allowed.
- A title TMDB has no US certification for follows the policy engine: with the default policy it needs approval, so it is shown and asking for it creates a pending request for a parent.

## Latency budget

| Step | Budget |
| --- | --- |
| Warm cache (search page and every certification cached) | target under 100 ms of server time |
| Cold cache, one page of 20 titles | about 1 TMDB search round trip plus 3 rounds of 8 parallel certification lookups; target under 2 s |
| Hard limit for certification lookups | 3 s; titles still unresolved are dropped |

Certifications are looked up concurrently, at most 8 at a time, through the TMDB client's cache: a title is fetched once and then cached for 7 days, search pages for 10 minutes, and failed lookups are not cached so they are retried on the next search. The browser waits 300 ms after the last key press before searching and cancels the request it no longer needs, so a fast typist sends one search per pause rather than one per letter.
