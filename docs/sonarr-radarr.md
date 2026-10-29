# Sonarr and Radarr

Wishlistarr can fulfil approved requests by adding titles directly to Sonarr (TV) and Radarr (movies). The backends live in `Wishlistarr.Integrations` behind the `IArrBackend` interface in `Wishlistarr.Core`. All endpoints below are parent only. There is no settings page in the web app yet; everything is done through the API. Both services must expose the v3 API (Sonarr v4 and Radarr v5 or later are the tested versions).

## Connecting

`PUT /api/v1/settings/sonarr` (or `radarr`) saves the connection:

```json
{ "url": "http://sonarr:8989", "apiKey": "...", "qualityProfileId": 4, "rootFolderPath": "/media/tv", "tagIds": [1] }
```

- `url` must be an `http` or `https` address with no username, query or fragment. A URL base such as `https://host/sonarr` is fine.
- The API key is stored encrypted and is never returned or logged. Leave `apiKey` out to keep the saved one; changing the address requires entering the key again. Keys containing control characters are rejected.
- `GET /api/v1/settings/sonarr` returns the saved values with `hasApiKey` instead of the key. `DELETE` forgets the connection.
- The `sonarr.*` and `radarr.*` settings cannot be written through the generic settings endpoint.

## Test button

`POST /api/v1/settings/sonarr/test` always answers `200` with `{ "valid", "message", "version" }`. The message says what is wrong: not configured, the API key was rejected, the address could not be reached, the address answered 404 (check the URL base), or the address is running a different application.

`POST /api/v1/settings/sonarr/test-candidate` (and the same under `radarr`) checks `{ "url", "apiKey" }` without saving it and answers the same way, or `400` when the values would not be accepted for saving. Leaving `apiKey` out reuses the saved key, but only when the address is the saved one, so a saved key is never sent to a new address.

## Choosing options

`GET /api/v1/sonarr/profiles`, `/rootfolders` and `/tags` (and the same under `/radarr`) list what the backend offers. They return `409` when the connection is not saved yet and `502` with a message when the backend fails. The chosen quality profile and root folder are required before a title can be fulfilled; the selected tags are applied in addition to the `wishlistarr` tag.

## Fulfilment

`IArrBackend.FulfilAsync` takes the TMDB id, the kind and, for shows, the requested season numbers.

- The `wishlistarr` tag is created in the backend when it does not exist (matched case-insensitively) and is put on every title Wishlistarr touches.
- A title already in Sonarr or Radarr is never added again. It is set to monitored, tagged, and searched (a season search for each requested season, or a movie search when the movie has no file yet).
- A new show is added with the chosen profile, root folder and tags, only the requested seasons monitored, and a search when it is added.
- A new movie is added monitored with the chosen profile, root folder and tags, and a search when it is added.

- An empty list of seasons means the whole show: every season except specials (season 0).
- The title is looked up in the backend before it is added, and a retry finds it there. When the add is refused because the title is already in Sonarr or Radarr, which both answer `400` with the `SeriesExistsValidator` or `MovieExistsValidator` error code (the validators' message is "This series has already been added" or "This movie has already been added"), the backend lists the title again and treats it as reused, so two attempts never produce two titles.

Approved requests are sent here when `fulfilment.mode` is `arr`. See [requests.md](requests.md#dispatching).
