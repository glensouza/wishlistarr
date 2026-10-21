# TMDB

Wishlistarr reads titles, ratings and artwork from [The Movie Database](https://www.themoviedb.org/). You need your own TMDB API key (v3 key or v4 read access token). A key containing a `.` is treated as a v4 read access token and sent as a bearer credential; anything else is sent as a v3 `api_key`.

## Configuration

Store the key in the `tmdb.api_key` setting with `PUT /api/v1/settings/tmdb.api_key`. It is always stored encrypted, whatever `isSecret` says, and is never returned. `POST /api/v1/settings/tmdb/test` checks the stored key against TMDB and returns `{ "valid", "message" }`. Both endpoints are parent only.

## Behavior

- Every request sends `include_adult=false`.
- Responses are cached in a dedicated in-memory cache limited to 2000 entries (`CacheMaxEntries`): searches for 10 minutes, discover and trending for 1 hour, details, credits, videos, images and watch providers for 12 hours. Certifications are cached per title for 7 days, so changing the region order does not cause another request. Failed responses are not cached.
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

Ids that Plex reports, request ids and states, and library titles (a kind plus a TMDB id, nothing else) are not TMDB metadata and are not subject to the retention. Posters and other images are proxied from TMDB per request and are never written to disk. The in-memory response cache holds entries for at most 7 days and is emptied when stored TMDB data is cleared.

The `tmdb-data-purge` job runs daily. It first refreshes records that are still in use and older than half the retention (a library id for an item still in the library, the title of a request that is still open), then deletes everything older than the retention. If TMDB cannot be reached, nothing is refreshed and expired records are deleted anyway.

Retention is the `tmdb.retention_days` setting: a whole number of days, 90 by default and at most 180. `PUT /api/v1/settings/tmdb.retention_days` rejects anything else.

A parent can remove all stored TMDB data at any time with `POST /api/v1/settings/tmdb/clear-data`, which returns `{ "purged": n }`. Removing the API key, by sending an empty value to `PUT /api/v1/settings/tmdb.api_key`, deletes the key and purges the same data.

Any new table or column that stores TMDB data must be marked with `[TmdbData]`, have a nullable `FetchedAt` column and be covered by an `ITmdbDataStore` so the purge job handles it. A test fails when a marked entity lacks either, or when an unmarked entity has columns that look like TMDB metadata.
