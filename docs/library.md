# Library index

Parents can preview and label the existing library in bulk with [library backfill labels](library-backfill.md).

Wishlistarr keeps a local copy of the movies and shows on the selected Plex server so it can tell instantly whether a title is available, without asking Plex each time. Music and photo libraries are not indexed.

## What is stored

- **Items**: one row per Plex movie or show with its server, rating key, guid, library, title, year, Plex's last-changed time and the time Plex added it.
- **Titles**: one canonical record per TMDB title (kind plus TMDB id, nothing else). Items that Plex lists more than once, for example a 1080p and a 4K copy, share one title.
- **External ids**: for every item, the TMDB, TVDB and IMDb ids Plex reports, plus the Plex guid and rating key. Lookups by TMDB id go through a unique index on the title record; lookups by TVDB or IMDb id go through an index on the id map.
- **Labels**: every label an item carries in Plex, not only the ones Wishlistarr applied. Labels are compared case-insensitively.
- **Refresh state**: the selected server, when the last full and incremental refreshes finished, and the cursor the next incremental refresh starts from.

Anything derived from TMDB carries a `FetchedAt` timestamp. Today that is the TMDB id of an item that Plex only knows through a legacy agent. Ids and data read from Plex do not have one.

## Legacy agent items

Items matched by a legacy Plex agent often have only a TVDB or IMDb id. During a refresh, an item without a TMDB id is resolved through TMDB's find endpoint, trying TVDB first and then IMDb, and the result is stored with its `FetchedAt`. A resolved id is kept until Plex reports a different TVDB or IMDb id for the item or starts reporting a TMDB id itself. Items that cannot be resolved stay in the index without a title and are retried at the next full refresh, or sooner if Plex changes them. If no TMDB API key is configured, resolution is skipped for that run.

## Refreshing

Two jobs run through the [job runner](jobs.md):

| Job | Default interval | What it does |
| --- | --- | --- |
| `library-full-refresh` | 1 day | Reads every movie and show, then (only when Plex returned everything it announced and the scan was not empty) removes items Plex no longer lists, items of a previously selected server and titles nothing points to. |
| `library-incremental-refresh` | 15 minutes | Reads only items Plex changed since the cursor and updates them. Runs a full refresh instead when none has completed yet or the selected server changed. |

The two jobs never run at the same time. Both finish quietly when Plex is not linked or no server is selected. Change the intervals or turn a job off under `Jobs:Schedules` as described in the job documentation; a parent can also start either job with `POST /api/v1/jobs/{name}/run`.

A Plex `library.new` [webhook](webhooks.md) refreshes the announced item straight away, resolving a season or episode to its show. When the event does not name an item, or another refresh is already running, an incremental refresh is started instead so webhook processing never waits. Plex does not announce label edits.

## How labels are read

Plex section listings do not include labels; they only appear on an item's detail. Reading every item would cost one request per item, so each full and incremental refresh instead asks every movie and show library for its labels and then lists the items carrying each label (`/library/sections/{id}/all?label={key}`). That is a few requests per label rather than one per item, and it does not depend on Plex changing an item's last-changed time when a label is edited, so label changes are picked up by the next incremental refresh. If Plex returns fewer items for a label than it announced, the stored labels are left as they were for that run. A webhook item refresh reads the item detail, which carries its labels.

Until the first full refresh completes the index reports that it cannot answer and requests fall back to asking Plex directly. The [home](kid-home.md) row "new in our library" asks the index for the newest distinct TMDB titles on the ready server, ordered by the time Plex added the item (`PlexAddedAtUnix`) and then by id. A metadata refresh in Plex does not move a title up, and the index's own `IndexedAt` is rewritten on every refresh so it is not used. Items indexed before this time was stored have none until the next full refresh reads them again; until then they are listed after items that have one. Items with no TMDB title are skipped. A different server's items are skipped. Before the first full refresh the index cannot answer, and that row is left out instead of asking Plex.

Each refresh also crawls labels: one request per movie and show library for its label list, then one or more per label for the items carrying it, so the cost grows with the number of distinct labels rather than the number of items.

## Status

`GET /api/v1/system/status` includes a `library` object:

```json
{
  "ready": true,
  "serverId": "4b1e0c9d8a7f6e5d4c3b2a1908f7e6d5c4b3a291",
  "lastFullRefreshAt": "2026-10-21T03:00:00+00:00",
  "lastIncrementalRefreshAt": "2026-10-21T09:15:00+00:00",
  "lastFullAttemptAt": "2026-10-21T03:00:00+00:00",
  "lastFullAttemptComplete": true,
  "itemCount": 1204,
  "unresolvedCount": 3,
  "labelCount": 5
}
```

`ready` is false until a full refresh has completed. `lastFullAttemptAt` and `lastFullAttemptComplete` describe the latest full refresh attempt; `false` means Plex returned less than it announced (or an unexpectedly empty library), so nothing was removed and the index was not marked refreshed. `unresolvedCount` is the number of items with no TMDB title, and `labelCount` is the number of distinct labels in use. The two refresh jobs and their last runs are listed under `jobs`.
