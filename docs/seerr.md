# Seerr

Wishlistarr can attach to requests in Seerr, and in Overseerr or Jellyseerr which speak the same API, on behalf of a configured user. The client lives in `Wishlistarr.Integrations` behind the `ISeerrBackend` interface in `Wishlistarr.Core`. All endpoints below are parent only. There is no settings page in the web app yet; everything is done through the API.

## Connecting

`PUT /api/v1/settings/seerr` saves the connection:

```json
{ "url": "http://seerr:5055", "apiKey": "...", "userId": 1 }
```

- `url` must be an `http` or `https` address with no username, query or fragment. A URL base such as `https://host/seerr` is fine.
- The API key is stored encrypted and is never returned or logged. Leave `apiKey` out to keep the saved one; changing the address requires entering the key again.
- `userId` is the Seerr user requests are made for. It must be a positive number.
- `GET /api/v1/settings/seerr` returns the saved values with `hasApiKey` instead of the key. `DELETE` forgets the connection.
- The `seerr.*` settings cannot be written through the generic settings endpoint.

## Test button

`POST /api/v1/settings/seerr/test` uses the saved connection. It answers `200` with `{ "version", "warning" }` when Seerr reports its version (`version`, or `commitTag` when version is absent). `warning` is set when the configured user is not an admin and does not have auto-approve, so requests made for them will wait for approval. It answers `409` when nothing is saved and `502` when Seerr cannot be reached, rejects the key, or has no such user.

## Requests

`POST /api/v1/seerr/requests` with `{ "kind": "movie", "tmdbId": 603 }` or `{ "kind": "show", "tmdbId": 1399, "seasons": [1] }` attaches to an existing Seerr request for that title, or creates one as the configured user when none exists. The response is `{ "id", "status", "attached" }`. Status is `pending`, `approved` or `declined` (Seerr values 1, 2 and 3). Any other status number is returned as that number.

`GET /api/v1/seerr/requests/{id}` reads the request status again for tracking.

Both return `409` when the connection is not saved and `502` with a message when Seerr fails. Calls go only to the saved address and are not followed to another host. Dispatching approved Wishlistarr requests is not part of this change.
