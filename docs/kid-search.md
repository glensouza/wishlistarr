# Kid search

Kids look for movies and shows with search as you type. `GET /api/v1/kid/search?q=<text>&page=<n>` needs a Kid session and always answers for the signed-in profile; there is no way to ask for another profile. A result links to the title screen (see [Title details](title-details.md)). Posters are served to kids from `GET /api/v1/kid/images/tmdb/{size}/{file}`, which uses the same proxy as the parent one (see [TMDB](tmdb.md#image-proxy)). Kids can request `w342` (search and title posters), `w185` (people and season posters) and `w1280` (backdrops). Any other size answers `400`. For those sizes the proxy serves any file name that passes the name check, not only files that appeared in an allowed title's response. Restricting it would need a per-profile record of every file name handed out, kept across restarts and instances, and an allowed title that is masked as needing approval would still need its own exceptions. It is acceptable because the host is fixed, redirects are not followed, only JPEG and PNG up to 10 MB are returned, a signed-in kid is required, and TMDB images are public artwork that is not tied to a title the policy blocked: knowing a file name already requires finding it elsewhere.

## Query rules

- The query is trimmed and runs of whitespace are collapsed. It must be 2 to 100 characters and contain no control characters, otherwise the answer is `400` and TMDB is not called.
- `page` is 1 to 10 and defaults to 1. Each page is one page of TMDB results (up to 20), so a page can hold fewer titles than that after filtering.
- Only movies and shows are returned; people and other result types are dropped.

## Response

`{ query, page, hasMore, results }`. Each result has `kind`, `tmdbId`, `title`, `year`, `overview`, `posterUrl`, `availability` and `needsParent`. There are no totals and no counts of hidden titles. `hasMore` comes from TMDB's own page count, so it says nothing about what was filtered out. Responses are sent with `Cache-Control: private, no-store`.

`availability` is `inLibrary` or `notInLibrary` when a library index can answer, and `null` when there is no index or it has not synced yet. Search never asks Plex directly.

Failures answer `502` (TMDB unreachable) or `503` (no API key) with a message that is safe to show a kid.

## Filtering

Every result is checked with the same content policy and US certification lookup as [requests](requests.md#asking-for-a-title). [Home](kid-home.md) uses that same pipeline and keeps only titles the policy allows. Search results supply TMDB genre ids (`genre_ids`) and the original language. When the profile blocks keywords, each title's keyword ids are read from `GET /movie/{id}/keywords` or `GET /tv/{id}/keywords` in the same lookup pass as its certification. A profile with no keyword blocks does not call that endpoint. Descriptors come from the certification already loaded and are passed only when the profile blocks descriptors. DoesTheDogDie is called only when a key is saved and the profile blocks at least one topic, and not when the title is on an explicit allow or block list. Otherwise search makes no DoesTheDogDie call. The response does not include topic names, the attribution link or a parent note.

- `Blocked` titles are removed before anything is returned. A keyword id on the profile's list blocks the title. A successful keyword lookup that returns none does not. A descriptor or topic on the profile's list blocks the title the same way. A successful lookup that found none is not a match.
- Titles whose certification cannot be read, because TMDB failed for that title or the lookup did not finish within the budget below, are removed. The same applies to a keyword lookup when the profile has keyword blocks, and to a topic lookup when the profile blocks topics and a key is saved, or when the profile blocks topics and no key is saved. Unknown never counts as allowed. The kid is not told which descriptor or topic matched.
- A title TMDB has no US certification for follows the policy engine: with the default policy it needs approval. A title with no genre ids does too, when the profile blocks genres. It is still returned so the kid can ask for it, but masked: `overview` and `posterUrl` are `null` (the poster path and text never leave the server) and `needsParent` is `true`. Asking for it creates a pending request for a parent. For every other result `needsParent` is `false`.

A lookup that fails is logged at debug level with the title it was for, and a search that runs out of time logs a warning.

## Latency budget

| Step | Budget |
| --- | --- |
| Warm cache (search page and every certification cached) | target under 100 ms of server time |
| Cold cache, one page of 20 titles | about 1 TMDB search round trip plus 3 rounds of 8 parallel certification lookups; target under 2 s. Keyword lookups, when the profile has them, run in those same rounds |
| Hard limit for certification, keyword and topic lookups | 3 s; titles still unresolved are dropped |

The 3 s limit covers the certification lookups, keyword lookups when the profile blocks keywords, and DoesTheDogDie lookups when a key is saved and the profile blocks topics. The TMDB search call itself is not bounded by it; it follows the TMDB client's own HTTP timeout and retries.

Certifications are looked up concurrently, at most 8 at a time, through the TMDB client's cache. A certification is fetched once and then cached for 7 days, search pages for 10 minutes, and keywords for 12 hours. Failed lookups are not cached, so they are retried on the next search. When the profile blocks keywords, each title's keyword lookup is part of that same pass and the same limit of 8. A DoesTheDogDie lookup, when a key is saved and the profile blocks topics, is part of that same pass. Its own cache lasts at most 7 days and is not the TMDB cache. See [Advisories](advisories.md). The browser waits 300 ms after the last key press before searching and cancels the request it no longer needs, so a fast typist sends one search per pause rather than one per letter.
