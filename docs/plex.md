# Plex

Wishlistarr works with Plex through the `IMediaServer` and `IMediaServerAccount` interfaces in `Wishlistarr.Core`. The Plex implementation lives in `Wishlistarr.Integrations` and only its options and service registration are public, so the rest of the app never sees Plex types. All endpoints below are parent only. There is no settings page in the web app yet; everything is done through the API.

Wishlistarr is not affiliated with or endorsed by Plex.

## Linking an account

Linking uses the plex.tv PIN flow, so nobody has to find or type a token.

1. `POST /api/v1/plex/link` returns `{ "id", "code", "authUrl", "expiresAt" }`.
2. Open `authUrl` in a browser and sign in to Plex to approve the request.
3. Poll `POST /api/v1/plex/link/{id}/check` every few seconds. It returns `{ "state": "pending" }` until the request is approved, then `{ "state": "linked", "accountName" }`, or `{ "state": "expired" }` once the code has run out. Unknown ids return `404`. Pending requests are kept in memory, so a restart means starting again.

`GET /api/v1/plex/status` reports whether an account is linked, the account name, the client identifier and the selected server. `DELETE /api/v1/plex/link` forgets the account and the selected server.

Two checks of the same approved link can run at the same time. Both report linked, and the sign-in is stored once.

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

When the saved connection stops answering, the next request runs the selection again and saves whichever connection answers. The server address and access token are written together, so a request never uses a new address with the previous token, or the previous address with a new token. If selection fails because nothing can be reached, further attempts wait 2 seconds, then 4, 8, and so on, up to 1 minute. A request that arrives during that wait fails without probing again. A connection that answers clears the wait, and so does choosing a server, linking or unlinking. Requests that failed together share one selection.

The addresses plex.tv lists for a server are controlled by whoever owns that server. When the linked account only has access to a shared server, the owner decides which connections appear, so a shared server can be unreachable even though the account can see it.

Server addresses only come from plex.tv for the chosen server. They must be plain `http` or `https` base addresses with no credentials, path, query or fragment, redirects are not followed, and the generic `PUT /api/v1/settings/{key}` endpoint refuses every `plex.` key. There is no way to enter a server address by hand.

## Notes from a live account

The plex.tv endpoints are undocumented, so the client and the test fixtures follow what a real account returns.

- A server can list many connections, mostly local HTTPS addresses on its other network interfaces. Only some answer from where Wishlistarr runs, which is why every candidate is probed and checked against the machine identifier.
- Servers list HTTPS connections on `plex.direct` host names. Local ones are marked `local` and the relay is marked `relay`.
- Managed users in `/api/users` have no `username` or `email` attribute, and are also marked `home="1"` and `restricted="1"`. Plex Home users in `/api/home/users` carry `admin`, `guest` and `restricted`; guests have an empty `username`.
- Libraries that use the `com.plexapp.agents.none` agent have no external guids and never match a lookup.
- Devices such as phones appear in the resources list without an access token and are skipped because they do not provide `server`.
- The PIN flow was checked with one throwaway PIN that was never claimed. Creating it answers `201`, and reading it back answers `200` with the same shape and `authToken: null` until it is approved. Its location block also carries `in_privacy_restricted_region`.
- Shared servers were not verified live, because the account used only owns its servers. The listed connections for a shared server follow the owner's settings, as described above.

## Settings and secrets

The account token (`plex.token`) and the server access token (`plex.server_token`) are stored encrypted like other secret settings and are never returned by the API. HTTP client logging is disabled for the Plex clients, and tokens are sent in the `X-Plex-Token` header rather than the URL.

## Connection test, libraries and users

- `POST /api/v1/settings/plex/test` returns `{ "valid", "message", "serverName", "version" }`. It reports a missing link, a missing server or a rejected token as `valid: false`, and answers `502` when Plex cannot be reached.
- `GET /api/v1/plex/libraries` lists the server's libraries with their id, title, type (`movie`, `show`, `music`, `photo` or `other`) and agent.
- `GET /api/v1/plex/users` lists Plex Home users (`/api/home/users`) and users the server is shared with (`/api/users`), each once, with `isHomeUser`, `isManaged` and `isAdmin`. This list is used later to map kid profiles to Plex users.

Endpoints that need a linked account or a selected server answer `409` until that is done.

## Lookup by external id

`GET /api/v1/plex/lookup?kind=movie&source=tmdb&id=603` finds items on the selected server. `kind` is `movie` or `show` (TMDB uses separate id ranges for movies and TV), `source` is `tmdb`, `tvdb` or `imdb`.

Items matched by the current Plex agents carry `tmdb://`, `tvdb://` and `imdb://` guids. Items matched by the legacy agents carry a single `com.plexapp.agents.themoviedb://`, `com.plexapp.agents.thetvdb://` or `com.plexapp.agents.imdb://` guid. Both are recognised. Other legacy agents and unmatched items are ignored.

Plex cannot search by external id, so the first lookup for a kind reads every library of that kind, 500 items per request, and keeps the result in memory for 10 minutes.

The [library index](library.md) answers the same question from a local copy once its first full refresh has completed.

## Labels

Wishlistarr tags movies and shows with each kid profile's label so Plex content filters can limit what that kid sees. `LabelWriter` adds and removes labels through `IMediaServer.GetLabelsAsync` and `SetLabelsAsync`; there is no endpoint for it yet. The [label guardian](#label-guardian) checks the result every day.

- TV labels are always applied to the show. A season or episode id is resolved to its show first.
- Every write reads the item's current labels, sends the complete set back (`label[0].tag.tag=...`) and locks the label field (`label.locked=1`) so a metadata refresh does not drop it. Clearing the last label uses `label[].tag.tag-=...` instead, because an empty set cannot be sent.
- Plex has no conditional write, so the labels are read a second time just before writing and the write starts over if they changed. After writing, the item is read again; if the change did not stick (for example another client wrote an older set at the same moment) the write is retried, up to 3 attempts.
- Writes to the same item are serialized inside the app.
- Labels on a Plex item are compared case-insensitively. Ledger labels (below) are matched with SQLite's `NOCASE` collation, which folds only the ASCII letters A to Z, so `Élodie` and `élodie` are different ledger labels. Code that checks ownership in memory uses the same rule (`LabelCollation`).
- Item reads ask for `includeGuids=1`, as python-plexapi does when it reloads an item, so the item's `tmdb://`, `tvdb://` and `imdb://` guids come back with its labels.

Whether Plex users enforce these labels is described in [plex-restrictions.md](plex-restrictions.md).

Every label the app applies is recorded in the `OwnedLabels` table with the server id, the item guid, the rating key, the label, the kid profile, the source of the change and the time. Items are matched by guid, so the record survives Plex assigning a new rating key. A label that was already on the item is not recorded, and removal only ever touches labels that are in this table, so labels added by hand in Plex are left alone.

## Lookup by guid

`IMediaServer.FindByGuidAsync` finds the items that carry a Plex agent guid (`plex://movie/...`, `plex://show/...`) by asking every movie and show library for `/library/sections/{id}/all?guid=...`, the same filter python-plexapi's `LibrarySection.getGuid` uses for these guids. It is one request per library (more when a page fills). Any other guid, such as a legacy `com.plexapp.agents.imdb://...` guid, returns nothing without a request, because the section filter is only documented for the Plex agents.

## Label guardian

The label guardian checks that the labels in the ownership ledger are still on their items in Plex, follows items that moved and puts back owned labels that went missing. It runs as the `label-guardian` [job](jobs.md) once a day; `label-guardian-dry-run` does the same checks without changing anything and only runs when a parent starts it (`POST /api/v1/jobs/label-guardian-dry-run/run`).

Each run, for the selected server:

1. Reads which items carry which labels with the same label crawl the [library index](library.md#how-labels-are-read) uses. Archived profiles and their ledger rows are skipped.
2. For each ledger row of an active profile, reads the item at the stored rating key. Plex gives an item that is removed and added again a new rating key, and the old one answers 404. If the stored key does not answer with the ledger's guid, the guardian looks for the guid among the library index's items and then, if that finds nothing, asks Plex by guid (see above). If the guid has gone, for example because the item was added again and matched to a new guid, the guardian uses the TMDB, TVDB and IMDb ids that the library index still holds under the ledger's guid. It never uses ids from the index row with the ledger's rating key: Fix Match keeps the rating key and gives the item a new guid, so after a refresh that row describes whatever the item was matched to. Every index item with one of those ids is a candidate. A candidate is read from Plex with its external guids and only kept when Plex reports one of the known ids for it, of the same kind, and no id from a source where it disagrees with all of the known ids. Exactly one candidate must remain. An item that cannot be found this way, for example because Fix Match changed it to another title or because the index no longer holds any row with the ledger's guid, is reported as `unresolved` with its ledger row left alone, and so are external ids that match more than one item.
3. A ledger row that points at a moved item is reported as `moved` and gets the new rating key (and guid). If another ledger row already owns the label on the new guid, the row is reported as `unresolved` instead. If such a row appears while the guardian is running (a grant made between the check and the update), the move is recorded as `failed` with the same detail, its repairs are skipped and the run carries on.
4. Every copy with the ledger's guid that does not carry the ledger's label is reported as `missing`, and the label is put back through the label writer with the same read, write and verify steps and per-item lock as any other label write. At the moment of writing, the repair checks that the ledger still owns the label on the item's guid and that the owning profile is still active; otherwise it is `skipped`. It never creates a ledger row, so a label revoked, or a profile archived, while the guardian runs stays off. The row keeps its source and time.
5. An item carrying an active profile's label with no ledger row for that guid and label is reported as `extra` and never touched; that is a label someone added by hand. The guid is the one Plex reported during this run (from the item read or the label crawl), never the library index's copy, which can lag behind a changed match. Extras are only checked when the label crawl returned everything Plex announced (`extrasChecked`).

Everything is read before anything is written. If Plex fails or cannot be reached while the guardian is reading, the run fails, nothing is recorded as drift and nothing is repaired. If Plex stops answering during the repairs, the remaining repairs are marked `skipped` and not tried; one timeout is enough, which is deliberately cautious. Each repair has the `Approval:LabelTimeout` deadline. A repair that times out is `failed`, and its detail says the label may or may not be on the item, because Plex may have taken the write. If the application stops during a repair, that repair is `interrupted` for the same reason, and the repairs after it are `skipped`. Once repairs have started the report is always saved: if an unexpected error stops them, the report is saved with the repair in progress marked `interrupted` and the rest `skipped`, and the run then fails. Requests to Plex are made one at a time, and each item is read once per run.

A dry run makes no Plex write and no ledger write. Its report is stored with `dryRun: true`, and its findings carry `wouldRepair` and `wouldUpdate` instead of `repaired` and `updated`. The scheduled job can be made to run dry with `LabelGuardian:DryRun`.

```json
{
  "LabelGuardian": {
    "DryRun": false,
    "HistoryRetention": "180.00:00:00"
  }
}
```

Reports are kept in `LabelGuardianReports` and `LabelGuardianEntries`. Every run of the scheduled `label-guardian` job deletes reports older than `HistoryRetention`, also when `LabelGuardian:DryRun` makes it run dry, so history does not grow without limit while the job is dry. A `label-guardian-dry-run` started by a parent never deletes reports. The parent endpoints are:

- `GET /api/v1/labels/guardian/reports?limit=20&offset=0` lists reports newest first as `{ items, total, limit, offset }`. Each item has `id`, `startedAt`, `finishedAt`, `dryRun`, `ledgerChecked`, `extrasChecked` and the counts `missing`, `extra`, `moved`, `unresolved`, `repaired` and `failed`. `limit` is 1 to 100 and `offset` is 0 or more; anything else is `400`.
- `GET /api/v1/labels/guardian/reports/{id}` returns `{ report, profiles, entries }`: the same summary, the counts per kid profile, and every finding with its profile, `kind` (`missing`, `extra`, `moved`, `unresolved`), `action` (`none`, `repaired`, `wouldRepair`, `updated`, `wouldUpdate`, `alreadyFixed`, `skipped`, `interrupted`, `failed`), label, guid, rating key, previous rating key and guid for a move, title and a plain-language `detail`. An unknown id is `404`.

What this relies on, and where it comes from: a rating key is a local database id that Plex assigns again when an item is removed and added back (reported in the Plex forum thread "Plex keeps changing rating key number"); a missing rating key answers 404 (python-plexapi's `PlexServer.query` maps a 404 to `NotFound`); and a `plex://` guid comes from the metadata match, so matching the same title again gives the same guid. That last point is not stated by Plex and was not checked against a live server, because only read-only requests were allowed. When it does not hold, the external-id step above still finds the item while the library index holds its old ids. The external-id step also relies on the guids Plex reports for an item (`includeGuids=1`) describing the title it is matched to now.
