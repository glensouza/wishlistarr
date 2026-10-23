# Health checks, logs and metrics

Parents can see whether each integration is healthy, read recent log lines, and look through a kid's request history. None of this is shown to a kid. `GET /api/health` is unchanged: it stays public and returns only `{ "status": "ok" }`.

## Health checks

`GET /api/v1/system/health` requires a parent (API key or parent session). It returns eight checks, always in this order: `plex`, `tmdb`, `seerr`, `sonarr`, `radarr`, `webhooks`, `jobs`, `database`. Each item is `{ integration, status, message, fix }`. `status` is `ok`, `warning` or `error`. `message` is a short description and `fix` says what to change. Messages are passed through the same redaction as the log viewer, so a token or API key that slips into an upstream error is not returned.

Each check runs in its own scope and gives up after 8 seconds. A check that does not answer, or that throws, is an `error` with a fixed message. The detail of the exception is not included.

| Check | `ok` | `warning` | `error` |
| --- | --- | --- | --- |
| Plex | The saved account can reach the selected server, and every mapped user's restrictions enforce the profile label. | Plex is not linked, no server is selected, or a mapped user does not enforce the label. Unknown restriction state is not a warning. The label text is the same one `GET /api/v1/system/status` already returns. | The selected server rejects the sign-in or cannot be reached. A label warning is not added on top of this. |
| TMDB | `GET /3/configuration` accepts the saved v3 key. | `tmdb.api_key` is not set. | TMDB rejects the key, or the call fails or times out. |
| Seerr | Seerr is not configured, or the saved user can request movies and shows. Not configured is healthy because Seerr is optional. | The saved user is missing request or auto-approve permission. The text is the same warning the Seerr test button returns. | The saved address or API key is rejected, or Seerr does not answer. |
| Sonarr, Radarr | Not configured, or `GET /api/v3/system/status` answers as that app and accepts the API key. Not configured is healthy because both are optional. | — | The saved connection is rejected, the process answers as the wrong app, or it cannot be reached. |
| Inbound webhooks | No webhook event has been waiting longer than 2 minutes, and the last `webhook-events` run did not fail. | An event is still unprocessed after 2 minutes. A newly received event is not a failure. The message does not include the token or the payload. | The last `webhook-events` run failed. |
| Background jobs | No registered job's latest run failed. | — | One or more jobs failed on their latest run. The message names the job. A single failure also includes the stored error, redacted. |
| Database | The SQLite file can be opened. | — | It cannot. The message does not include the path or the exception text. The fix names `WISHLISTARR_DATA_DIR`. |

The parent page loads this list and shows a banner when any check is `warning` or `error`. The banner includes the message and the fix. Kid screens do not request it and do not render it.

`GET /api/v1/system/status` still has its own `warnings` list for labels that are not enforced. That list is separate from this report.

## Logs

Wishlistarr keeps a bounded in-memory ring of recent log lines and also writes the daily files under the data directory's `logs` folder. The viewer reads the ring, not the files. The ring holds 1000 lines unless `Logs:Capacity` (or `Logs__Capacity`) sets another size from 1 to 5000. Older lines are dropped. The process forgets the ring when it stops.

The logging pipeline writes into the ring before the console and file sinks. String properties whose names are secrets (`password`, `pin`, `token`, `secret`, `apiKey`, `api_key`, `accessToken`, `access_token`, `authorization`, `X-Api-Key`, `X-Plex-Token`) are replaced with `***`, and the message text is redacted, before the line is stored. The same replacement is applied to the property values later sinks render. An exception's message is redacted in the ring. The redaction covers:

- JSON fields `password`, `pin`, `apiKey`, `api_key`, `apikey`, `token`, `access_token`, `accessToken`, `authorization`, `X-Plex-Token` and `X-Api-Key`
- `Authorization`, `X-Plex-Token` and `X-Api-Key` header values, including `Bearer` and `Basic`
- a standalone `Bearer` token
- `X-Plex-Token`, `X-Api-Key`, `api_key`, `apikey`, `apiKey`, `access_token`, `accessToken`, `password`, `pin` and `token` assignments in a query string or after whitespace
- the token segment of `/api/v1/webhooks/{source}/{token}`

The word "token" in ordinary text is left as it is.

`GET /api/v1/system/logs` returns `{ at, level, category, message }` from oldest to newest. `level` is `trace`, `debug`, `information`, `warning`, `error` or `critical`. The optional `level` query keeps that level and anything more severe. Omitting it returns the whole ring. Any other value is `400`.

`GET /api/v1/system/logs/download` returns the same lines as `text/plain`, attached as `wishlistarr.log`. Each line is the UTC timestamp, the level, the category and the message. The parent page links to it and filters with the same query. Both routes are parent-only.

## Request history

`GET /api/v1/kid-profiles/{id}/events` is parent-only. It reuses the `MediaRequestEvents` rows written when a request is created or moves state. Results are newest first. `limit` defaults to 50 and is clamped to 1–200. Archived profiles are included, because deleting a profile keeps the row. A profile id that does not exist is `404`.

Each item is `{ id, requestId, kind, tmdbId, title, fromState, toState, actorKind, actorName, reason, occurredAt }`. `fromState` is null for the event that created the request. No PIN is included. `GetHistoryAsync` on a single request is unchanged and still returns oldest first.

The parent page lists this under Request history, one block per profile.

## Metrics

OpenTelemetry metrics are registered in-process and are not exported anywhere. There is no OTLP exporter and no ASP.NET Core or HTTP client instrumentation. The only output is `GET /metrics`, and it is off unless `Metrics:Enabled` is `true` (`Metrics__Enabled` in the environment).

While it is off, `GET /metrics` still requires a parent and answers `404`. An anonymous caller gets `401` and a kid session gets `403`. The body is not a metrics document.

While it is on, the same route serves the Prometheus text exposition from the OpenTelemetry Prometheus exporter. It requires the API key or a parent session. Nothing is pushed to a collector. Scrape it only from a network that should see this data. The exporter does not secure the route by itself; Wishlistarr does.

Three instruments are recorded, on a meter named `Wishlistarr`:

- `wishlistarr.integration.health`, a gauge with one label, `integration`. The value is 0 for `ok`, 1 for `warning` and 2 for `error`. It updates when `GET /api/v1/system/health` runs.
- `wishlistarr.jobs`, a counter with labels `job` (the registered job name) and `outcome` (`succeeded`, `failed` or `cancelled`). It increments when a run finishes, not while the run is still `running`.
- `wishlistarr.requests`, a counter with labels `kind` (`movie` or `show`) and `state` (`pending`, `approved`, `denied`, `fulfilling`, `available`, `labeled` or `failed`). It increments after a request is saved or a transition is saved.

The Prometheus scrape uses the exporter's default names (`wishlistarr_integration_health`, and `_total` on the counters). Labels are only the ones above. Profile names, titles, Plex labels, PINs, tokens and API keys are not labels and are not values. The resource is the service name `wishlistarr` with no generated instance id, so a machine name is not attached.

The parent page does not scrape `/metrics`.
