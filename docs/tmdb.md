# TMDB

Wishlistarr reads titles, ratings and artwork from [The Movie Database](https://www.themoviedb.org/). You need your own TMDB API key (v3 key or v4 read access token).

## Configuration

Store the key in the `tmdb.api_key` setting with `PUT /api/v1/settings/tmdb.api_key`. It is always stored encrypted, whatever `isSecret` says, and is never returned. `POST /api/v1/settings/tmdb/test` checks the stored key against TMDB and returns `{ "valid", "message" }`. Both endpoints are parent only.

## Behavior

- Every request sends `include_adult=false`.
- Responses are cached in memory: searches for 10 minutes, discover and trending for 1 hour, details, credits, videos, images and watch providers for 12 hours. Certifications are cached per title for 7 days, so changing the region order does not cause another request. Failed responses are not cached.
- `429` responses are retried up to 3 times, waiting for `Retry-After` when present and backing off exponentially otherwise. Waits are capped at 30 seconds.
- Certifications come from `release_dates` for movies (theatrical releases preferred) and `content_ratings` for TV. The caller passes regions in priority order and gets the first one with a rating, along with its content descriptors.
- The key is never written to logs: HTTP client logging is disabled for TMDB clients.

## Image proxy

`GET /api/v1/images/tmdb/{size}/{file}` serves images from `image.tmdb.org`. Sizes must be one of `w45`, `w92`, `w154`, `w185`, `w300`, `w342`, `w500`, `w780`, `w1280`, `h632` or `original`, and the file must be a single name made of letters, digits, `_` and `-` ending in `.jpg`, `.jpeg` or `.png`. The host is fixed, redirects are not followed, only `image/jpeg` and `image/png` responses up to 10 MB are returned, and the endpoint requires a parent.
