# Background jobs

Recurring work runs as named jobs inside the application. A hosted scheduler checks every `Jobs:PollInterval` (default 15 seconds) and starts each job that is due. Jobs can also be started manually by a parent.

## Behaviour

- A job never runs concurrently with itself, whether the second start comes from the schedule or from a manual trigger.
- Every run is stored with its trigger, start time, end time, outcome (`running`, `succeeded`, `failed` or `cancelled`) and error message.
- On shutdown running jobs receive a cancellation signal and are recorded as `cancelled`. Runs left open by a crash are marked `failed` at the next start.
- A job is due when it has never run, or when its interval has passed since the start of its last run.

## Configuration

```json
{
  "Jobs": {
    "PollInterval": "00:00:15",
    "HistoryRetention": "30.00:00:00",
    "Schedules": {
      "job-history-cleanup": { "Enabled": true, "Interval": "1.00:00:00" },
      "database-backup": { "Enabled": true, "Interval": "1.00:00:00" }
    }
  }
}
```

`Schedules` entries are keyed by job name. `Interval` replaces the job's default interval and `Enabled: false` stops scheduled runs; a disabled job can still be triggered manually.

## API

All endpoints require a parent.

- `GET /api/v1/jobs` lists jobs with their schedule, running state and last run.
- `GET /api/v1/jobs/{name}/runs?limit=20` lists recent runs, newest first.
- `POST /api/v1/jobs/{name}/run` starts a job. Returns `202` with the run id, `404` for an unknown job, `409` when the job is already running and `503` during shutdown.

`GET /api/v1/system/status` includes the same job list.

## Registering a job

Implement `IBackgroundJob` and register it with `AddBackgroundJob<TJob>(name, description, defaultInterval)`. A `null` interval means the job only runs when triggered manually. Each run gets its own dependency injection scope.

The built-in `job-history-cleanup` job deletes finished runs older than `Jobs:HistoryRetention`.

The `database-backup` job writes a consistent copy of the database and the data protection key ring, then keeps the newest `Backups:Retention` files. The zip contains secrets. See [backup.md](backup.md).

The `tmdb-data-purge` job enforces the TMDB data retention described in [tmdb.md](tmdb.md).

The `request-dispatch` job sends approved requests to Seerr, Sonarr or Radarr. It is started after an approval or a retry (run trigger `Request`) and also runs every minute to pick up anything that was interrupted. See [requests.md](requests.md#dispatching).

The `update-check` job asks GitHub whether a newer release exists. It runs once a day. See [upgrading.md](upgrading.md). It is the only call Wishlistarr makes to GitHub by itself.
