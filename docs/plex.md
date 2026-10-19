# Plex

Wishlistarr works with Plex through the `IMediaServer` and `IMediaServerAccount` interfaces in `Wishlistarr.Core`. The Plex implementation lives in `Wishlistarr.Integrations` and only its options and service registration are public, so the rest of the app never sees Plex types. All endpoints below are parent only. There is no settings page in the web app yet; everything is done through the API.

Wishlistarr is not affiliated with or endorsed by Plex.

## Linking an account

Linking uses the plex.tv PIN flow, so nobody has to find or type a token.

1. `POST /api/v1/plex/link` returns `{ "id", "code", "authUrl", "expiresAt" }`.
2. Open `authUrl` in a browser and sign in to Plex to approve the request.
3. Poll `POST /api/v1/plex/link/{id}/check` every few seconds. It returns `{ "state": "pending" }` until the request is approved, then `{ "state": "linked", "accountName" }`, or `{ "state": "expired" }` once the code has run out. Unknown ids return `404`. Pending requests are kept in memory, so a restart means starting again.

`GET /api/v1/plex/status` reports whether an account is linked, the account name, the client identifier and the selected server. `DELETE /api/v1/plex/link` forgets the account and the selected server.

Wishlistarr identifies itself to Plex with a client identifier that is generated once and stored in the `plex.client_identifier` setting. It is kept when the account is unlinked, so Plex keeps seeing the same device.

## Choosing a server

`GET /api/v1/plex/servers` lists the servers the account can reach, with each server's advertised connections. `PUT /api/v1/plex/server` with `{ "serverId" }` selects one.

Connections are tried in this order:

1. local HTTPS
2. remote HTTPS
3. local HTTP
4. relay
5. remote HTTP

Plain HTTP is skipped entirely when the server requires secure connections. Within each group IPv4 is tried before IPv6. Each candidate is first asked for `/identity` without a token and is only used when it answers with the selected server's machine identifier, so the token is never sent to a device that is not that server. Each probe waits up to 5 seconds.

When the saved connection stops answering, the next request runs the selection again and saves whichever connection answers.

Server addresses only come from plex.tv for the chosen server. They must be plain `http` or `https` base addresses with no credentials, path, query or fragment, redirects are not followed, and the generic `PUT /api/v1/settings/{key}` endpoint refuses every `plex.` key. There is no way to enter a server address by hand.

## Settings and secrets

The account token (`plex.token`) and the server access token (`plex.server_token`) are stored encrypted like other secret settings and are never returned by the API. HTTP client logging is disabled for the Plex clients, and tokens are sent in the `X-Plex-Token` header rather than the URL.

## Connection test, libraries and users

- `POST /api/v1/settings/plex/test` returns `{ "valid", "message", "serverName", "version" }`. It reports a missing link, a missing server or a rejected token as `valid: false`, and answers `502` when Plex cannot be reached.
- `GET /api/v1/plex/libraries` lists the server's libraries with their id, title, type (`movie`, `show`, `music`, `photo` or `other`) and agent.
- `GET /api/v1/plex/users` lists Plex Home users and friends, each once, with `isHomeUser`, `isManaged` and `isAdmin`. This list is used later to map kid profiles to Plex users.

Endpoints that need a linked account or a selected server answer `409` until that is done.

## Lookup by external id

`GET /api/v1/plex/lookup?kind=movie&source=tmdb&id=603` finds items on the selected server. `kind` is `movie` or `show` (TMDB uses separate id ranges for movies and TV), `source` is `tmdb`, `tvdb` or `imdb`.

Items matched by the current Plex agents carry `tmdb://`, `tvdb://` and `imdb://` guids. Items matched by the legacy agents carry a single `com.plexapp.agents.themoviedb://`, `com.plexapp.agents.thetvdb://` or `com.plexapp.agents.imdb://` guid. Both are recognised. Other legacy agents and unmatched items are ignored.

Plex cannot search by external id, so the first lookup for a kind reads every library of that kind, 500 items per request, and keeps the result in memory for 10 minutes.
