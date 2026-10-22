# TMDB

Wishlistarr reads titles, ratings and artwork from [The Movie Database](https://www.themoviedb.org/). You need your own TMDB API key (v3 key or v4 read access token). A key containing a `.` is treated as a v4 read access token and sent as a bearer credential; anything else is sent as a v3 `api_key`.

## Configuration

Store the key in the `tmdb.api_key` setting with `PUT /api/v1/settings/tmdb.api_key`. It is always stored encrypted, whatever `isSecret` says, and is never returned. `POST /api/v1/settings/tmdb/test` checks the stored key against TMDB and returns `{ "valid", "message" }`. Both endpoints are parent only.

## Behavior

- Every request sends `include_adult=false`.
- Responses are cached in a dedicated in-memory cache limited to 2000 entries (`CacheMaxEntries`): searches for 10 minutes, discover and trending for 1 hour, details, credits, videos, images, watch providers and keywords for 12 hours. Certifications are cached per title for 7 days, so changing the region order does not cause another request. Failed responses are not cached. Keyword lists for a movie come from `GET /movie/{id}/keywords` (`keywords`) and for a show from `GET /tv/{id}/keywords` (`results`).
- `429` responses are retried up to 3 times, waiting for `Retry-After` when present and backing off exponentially otherwise. Each wait is capped at 30 seconds and the total wait at 45 seconds; after that the 429 is returned as an error.
- Certifications come from `release_dates` for movies (theatrical releases preferred) and `content_ratings` for TV. The caller passes regions in priority order and gets the first one with a rating, along with its content descriptors.
- The key is never written to logs: HTTP client logging is disabled for TMDB clients.

## Image proxy

`GET /api/v1/images/tmdb/{size}/{file}` serves images from `image.tmdb.org`. Sizes must be one of `w45`, `w92`, `w154`, `w185`, `w300`, `w342`, `w500`, `w780`, `w1280`, `h632` or `original`, and the file must be a single name made of letters, digits, `_` and `-` ending in `.jpg`, `.jpeg` or `.png`. The host is fixed, redirects are not followed, only `image/jpeg` and `image/png` responses up to 10 MB are returned, and the endpoint requires a parent.

## Stored TMDB data and retention

TMDB's terms do not allow keeping its data longer than six months, so everything derived from TMDB that Wishlistarr writes to its database carries the time it was fetched (`FetchedAt`) and is removed once it is older than the retention.

What is stored:

| Data | Where | Kept |
| --- | --- | --- |
| The TMDB id found for a Plex item that only has a TVDB or IMDb id | `LibraryExternalIds`, rows with origin `TmdbLookup` | Until the retention is reached, then deleted. The item is looked up again by the next library refresh. |
| The title text of a kid request | `MediaRequests.Title` with `TitleFetchedAt` | Until the retention is reached, then cleared. The request keeps its kind, TMDB id and status, and the title is fetched again from TMDB the next time it is needed. |

Ids that Plex reports, request ids and states, and library titles (a kind plus a TMDB id, nothing else) are not TMDB metadata and are not subject to the retention. Posters and other images are proxied from TMDB per request and are never written to disk. Keyword ids read for a content decision are not written to the database either; they stay in the response cache. The in-memory response cache holds entries for at most 7 days and is emptied when stored TMDB data is cleared.

The `tmdb-data-purge` job runs daily and waits for any running library refresh to finish before touching library data (clearing does the same). It first refreshes records that are still in use and older than half the retention (a library id for an item still in the library, the title of a request that is still open), then deletes everything older than the retention. If a refresh fails for any reason, including a TMDB timeout, expired records are deleted anyway, and each kind of data is purged independently so one failure never skips another.

The TMDB key is only ever supplied by the settings store, so removing it through the settings API as described below is the one path that purges immediately; if it disappears any other way, stored data still ages out through the retention.

Retention is the `tmdb.retention_days` setting: a whole number of days, 90 by default and at most 180. `PUT /api/v1/settings/tmdb.retention_days` rejects anything else.

A parent can remove all stored TMDB data at any time with `POST /api/v1/settings/tmdb/clear-data`. Removing the API key, by sending an empty value to `PUT /api/v1/settings/tmdb.api_key`, deletes the key and purges the same data.

Both run the purge independently of the HTTP request, so a client or proxy giving up never leaves data behind. The purge is also durable: a `system.tmdb_purge_pending` setting is written before it starts and removed only after it succeeds, so a purge interrupted by a restart, a shutdown or a failure is resumed at the next startup and by the next daily `tmdb-data-purge` run, which also purges all request titles and library lookups whenever no TMDB API key is configured. A failure while the request is still waiting answers `500` and the purge stays pending. Request titles are cleared first. Library data waits for any running library refresh, so when that takes more than a few seconds the endpoints answer `202` (`{ "purged": null, "pending": true }` for clear, an empty body for key removal) and the library data is deleted as soon as the refresh finishes. When nothing is waiting, clear answers `200` with `{ "purged": n, "pending": false }` and key removal answers `204`.

Request titles must be read through `RequestTitles`, which checks the age and fetches the title again when it has been purged; reading `MediaRequest.Title` directly can return nothing. Requests that existed before the retention was introduced have their title age taken from the request creation time, so titles older than the retention are cleared by the first purge and fetched again when they are shown.

Any new table or column that stores TMDB data must be marked with `[TmdbData]`, have a nullable `FetchedAt` column and be covered by an `ITmdbDataStore` so the purge job handles it. A test fails when a marked entity lacks either, or when an unmarked entity has columns that look like TMDB metadata.
