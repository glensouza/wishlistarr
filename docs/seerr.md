# Seerr

Wishlistarr can attach to requests in Seerr, and in Overseerr or Jellyseerr which speak the same API, on behalf of a configured user. The client lives in `Wishlistarr.Integrations` behind the `ISeerrBackend` interface in `Wishlistarr.Core`. All endpoints below are parent only. There is no settings page in the web app yet; everything is done through the API.

## Connecting

`PUT /api/v1/settings/seerr` saves the connection:

```json
{ "url": "http://seerr:5055", "apiKey": "...", "userId": 1 }
```

- `url` must be an `http` or `https` address with no username, query or fragment. A URL base such as `https://host/seerr` is fine and is kept for every call.
- The API key is stored encrypted and is never returned or logged. Leave `apiKey` out to keep the saved one; changing the address requires entering the key again.
- `userId` is the Seerr user requests are made for. It must be a positive number.
- `GET /api/v1/settings/seerr` returns the saved values with `hasApiKey` instead of the key. `DELETE` forgets the connection.
- The `seerr.*` settings cannot be written through the generic settings endpoint.

## Acting as the configured user

A Seerr API key on its own acts as the first admin account. New requests are therefore sent with the `X-Api-User` header set to the configured user id, so Seerr applies that user's permissions, quotas and auto-approval, and records them as the requester. The request body never carries `userId`, which Seerr only accepts from a caller with Manage Users or Manage Requests.

Reads (the status, the user lookup, title details and request tracking) are made with the key alone. They do not depend on the user's permissions, and tracking can then read a request another Seerr user made.

## Test button

`POST /api/v1/settings/seerr/test` uses the saved connection and ignores any address or key in the body. It reads `/api/v1/status`, which Seerr serves without a key, and reports `version`, or `commitTag` when version is absent. It then looks up the configured user with the key, which is where a wrong key is caught.

It answers `200` with `{ "version", "warnings" }`. `warnings` is a list, checked separately for movies and shows:

- The user cannot request that kind when they have none of Admin, Request, or Request Movies (Request Series for shows). Seerr will refuse those requests.
- Otherwise requests of that kind wait for approval when the user has none of Admin, Manage Requests, Auto-Approve, or Auto-Approve Movies (Auto-Approve Series for shows).

The permission bits are Admin 2, Manage Requests 16, Request 32, Auto-Approve 128, Auto-Approve Movies 256, Auto-Approve Series 512, Request Movies 262144 and Request Series 524288. 4K requests are not made, so the 4K permissions are not checked.

It answers `409` when nothing is saved and `502` when Seerr cannot be reached, rejects the key, or has no such user.

## Requests

`POST /api/v1/seerr/requests` takes `{ "kind": "movie", "tmdbId": 603 }` or `{ "kind": "show", "tmdbId": 1399, "seasons": [1, 2] }`. Season numbers are sent once each in ascending order. A show with no seasons, or an empty list, is requested with `"seasons": "all"`, which Seerr expands to every season except specials. Only non-4K requests are made.

Before creating anything, the title is read from Seerr and an existing request is attached when it matches:

- Declined requests and 4K requests are ignored.
- For a movie, a pending, approved or failed request is attached. A completed request is attached only while the movie is still available; when the movie was deleted, a new request is made.
- For a show, the requested seasons must all be covered, either by pending, approved or failed requests or by seasons Seerr already lists as pending, processing, partially available or available. When they are, a request that holds one of those seasons is attached. When some are missing, a new request is made and Seerr keeps only the missing seasons on it.
- When several requests match, the configured user's own request is preferred, then the oldest.
- A title that is already available without a matching request is not requested again. The response then has no `id` or `status`.

When another request is made between the read and the create, Seerr answers `409` (a movie that already has a request) or `202` (a show with no seasons left to request). The title is then read again and the matching request is attached.

The response is `{ "id", "status", "availability", "attached" }`. `attached` is true when an existing request was used.

## Tracking

`GET /api/v1/seerr/requests/{id}` answers `{ "id", "status", "availability" }`, so a title that has arrived can be detected.

`status` is the request status:

| Seerr value | Name |
| --- | --- |
| 1 | `pending` |
| 2 | `approved` |
| 3 | `declined` |
| 4 | `failed` |
| 5 | `completed` |
| other | `unknown` |

`availability` is the media status of the title, not of the request:

| Seerr value | Name |
| --- | --- |
| 1 | `unknown` |
| 2 | `pending` |
| 3 | `processing` |
| 4 | `partiallyAvailable` |
| 5 | `available` |
| 6 | `blocklisted` |
| 7 | `deleted` |
| other | `unknown` |

These are Seerr's values. Overseerr has no blocklist and uses 6 for deleted, so a deleted title in Overseerr is reported as `blocklisted`. Either way the title is not in the library and is not on its way.

## Errors

Both request endpoints return `409` when the connection is not saved and `502` with a message when Seerr fails. When Seerr refuses a request with its own reason, such as a missing permission or an exceeded quota, that reason is included. Calls go only to the saved address and are not followed to another host. Dispatching approved Wishlistarr requests is not part of this change.
