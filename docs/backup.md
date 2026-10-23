# Backups and restore

A backup is a zip of the SQLite database and the data protection key ring. The key ring is what decrypts secrets stored in the database, including the API key and service tokens. Anyone with the zip can read those secrets. Treat a backup file like a password: do not share it, do not put it somewhere a child can open it, and do not send it in chat or email. Downloads are parent-only and are sent with `Cache-Control: no-store`. Kid sessions cannot list or download backups, and the app does not write backup contents to the log.

Backups are stored under the data directory, so the same layout works for a Docker volume and a local run:

| Run | Data directory | Database | Key ring | Backups |
| --- | --- | --- | --- | --- |
| Docker (`WISHLISTARR_DATA_DIR=/data`) | `/data` | `/data/wishlistarr.db` | `/data/keys` | `/data/backups` |
| Local default | `data` under the application folder | `data/wishlistarr.db` | `data/keys` | `data/backups` |

The database copy is made with SQLite `VACUUM INTO`, which writes a consistent snapshot while the app is running and leaves the live database unchanged. That snapshot is a rollback-journal database. Wishlistarr enables WAL the next time it opens the file for writing. The zip then contains `wishlistarr.db` and the `keys/*.xml` files from the key ring. Nothing else is included.

## Schedule and retention

The `database-backup` job takes a backup and then keeps the newest `Backups:Retention` files (default 7). Older backup zips are deleted. The job is due once a day, and a parent can start it early with **Back up now** or `POST /api/v1/jobs/database-backup/run`. See [jobs.md](jobs.md) for intervals and manual runs.

```json
{
  "Backups": {
    "Retention": 7,
    "MaxUploadBytes": 536870912
  },
  "Jobs": {
    "Schedules": {
      "database-backup": { "Enabled": true, "Interval": "1.00:00:00" }
    }
  }
}
```

`Retention` is a whole number from 1 to 100. `MaxUploadBytes` caps a restore upload and defaults to 512 MiB. It cannot be raised above that.

## Before a migration

On startup, if the database file already exists and a migration is pending, Wishlistarr takes a backup before applying it. The migration does not run when that backup fails, and the process stops so the existing database is left as it was. A new data directory has no database to copy, so the first start migrates without a backup. Details of the migration policy are in [migrations.md](migrations.md).

## Restore

Restore is a parent action and the browser must send the antiforgery token. The Parents page accepts one zip. Before anything in the live data directory is changed, the upload is checked:

- the file is within the size cap
- every zip entry is a relative name with no `..`, drive prefix, or backslash
- the only entries are `wishlistarr.db` and `keys/*.xml` key files
- `PRAGMA integrity_check` on the database returns `ok`
- every migration id in the database is one this build knows. A backup from a newer version is refused

A zip that fails a check is rejected and the current database is not touched. A zip that passes is staged under `restore-pending`. The running process keeps serving the current database. On the next start, before the database is opened, the staged files replace the live database and key ring. The data that was in use is written to a backup zip first, so it can be restored again. Do not copy files over the live database while the app is running.

If a staged restore fails those checks again at startup, it is not applied and the process stops. Remove `restore-pending` from the data directory to start on the current data.
