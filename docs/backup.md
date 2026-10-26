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
    "MaxUploadBytes": 536870912,
    "MaxRestoreBytes": 2147483648,
    "MaxEntries": 64
  },
  "Jobs": {
    "Schedules": {
      "database-backup": { "Enabled": true, "Interval": "1.00:00:00" }
    }
  }
}
```

`Retention` is a whole number from 1 to 100. The other three are the limits restore enforces, and they default to the values shown:

- `MaxUploadBytes` caps the size of the zip file and cannot be raised above 512 MiB.
- `MaxRestoreBytes` caps the combined size of everything inside the zip, and the database in particular. It cannot be raised above 2 GiB.
- `MaxEntries` caps the number of files in the zip (the database plus one per key file) and is a whole number from 2 to 1024.

The app applies the same limits when it writes a backup. A backup that restore would refuse (too many key files, a database over `MaxRestoreBytes`, or a zip over `MaxUploadBytes`) is not kept: the job or startup step fails, an error naming the setting is logged, and the earlier backups are left in place. Raise the setting if the data really has grown that large.

Retention orders backups by the time and sequence number in the file name, not by the file's modified time, and the backup that was just written is always kept, even if the clock has gone backwards. Only files whose names match `wishlistarr-<yyyyMMddTHHmmssfffZ>-<6 digits>.zip` are counted or deleted, so other files in the backups folder are left alone. The same rule applies to the backups taken before a migration and before a restore.

A backup is written to a `.partial` file, flushed to disk and read back before it is renamed to its final name. The read back checks the entry names and sizes and decompresses every entry to compare its CRC-32 with the one stored in the zip, so a backup that was damaged while it was written is deleted instead of kept.

## Before a migration

On startup, if the database file already exists and a migration is pending, Wishlistarr takes a backup before applying it. The migration does not run when that backup fails, and the process stops so the existing database is left as it was. A new data directory has no database to copy, so the first start migrates without a backup. Details of the migration policy are in [migrations.md](migrations.md).

A backup needs the key ring. If the database exists but the `keys` folder is missing or has no key files, the pre-migration backup fails and startup stops on purpose: without the key ring the saved secrets cannot be decrypted, and migrating would make that harder to undo. To recover, stop the app and do one of these:

- put the `keys` folder back from the same install, or extract `keys/*.xml` from the newest backup zip into `keys` in the data directory, then start again
- if no copy of the key ring exists, the saved secrets cannot be decrypted by any build. Copy `wishlistarr.db` somewhere safe, then remove it from the data directory and start again. Wishlistarr starts as a new install with a new key ring, and the copied database is kept in case the key ring turns up later

## Restore

Restore is a parent action and the browser must send the antiforgery token. The Parents page accepts one zip and asks for confirmation, because a restore replaces all current data when the app next starts. Before anything in the live data directory is changed, the upload is checked:

- the file is within the size cap
- the zip has at most `MaxEntries` entries, and the declared sizes of all entries together are within `MaxRestoreBytes`. The bytes actually read are counted against the same limit as they are unpacked
- every entry decompresses to exactly its declared length and checksum, so a zip that lies about its sizes is refused
- every zip entry is a relative name with no `..`, drive prefix, backslash or leading `/`, no name appears twice, and no entry is a symlink or other link
- the only entries are `wishlistarr.db` and `keys/*.xml` key files, and both are present
- `PRAGMA integrity_check` on the database returns `ok`
- every migration id in the database is one this build knows. A backup from a newer version is refused

The upload is unpacked into a `restore-staging-<id>` folder in the data directory. A zip that fails a check is rejected with a 400 response, the staging folder is deleted, and the current database is not touched. A zip that passes is moved to `restore-pending`. The running process keeps serving the current database. Do not copy files over the live database while the app is running.

ASP.NET Core writes an upload larger than 64 KiB to a temporary file while the request is read and deletes it when the request ends. That file is a full copy of the backup, secrets included. It goes to the folder named by the `ASPNETCORE_TEMP` environment variable, or the system temp folder when that is not set. On a shared machine, set `ASPNETCORE_TEMP` to a folder only the Wishlistarr user can read, for example a `tmp` folder inside the data volume.

### Applying a restore at startup

On the next start, before the database is opened:

1. Any `restore-staging-<id>` folder left by a crash during an upload is deleted. Only folder names that match that exact pattern are removed.
2. The staged files are checked again. If they fail, the staged restore is deleted, the error is logged and this start stops. The current data is not changed, and the next start runs on it.
3. The current database and key ring are written to a backup zip, so they can be restored later. If that backup fails, for example because the current key ring is missing (see [Before a migration](#before-a-migration)), the staged restore is deleted, nothing is swapped and this start stops. Upload the zip again after fixing the cause.
4. The files are swapped. A journal file, `restore-journal.json`, is written first and records that a swap is in progress. The current database (with any `-wal`, `-shm` and `-journal` files) and `keys` folder are moved into `restore-previous`, then the staged database and key ring are moved into place. Once both are in place the journal is marked committed, and only then are `restore-pending` and `restore-previous` deleted and the journal removed.

If any step of the swap fails, the previous database and key ring are moved back together, the staged restore is deleted, and the start stops with an error. The next start runs on the previous data. If the process is killed part way through, the next start reads the journal: an uncommitted swap is rolled back so the previous database and key ring are live again and the restore is then tried again, and a committed swap is finished by deleting the leftovers. Either way the database and key ring always come from the same side, old or new, and running startup again after a recovery changes nothing.

If a committed swap could not delete its leftovers, the journal stays until a later start succeeds. While a journal exists a new restore upload is refused with a 409 response, and the message asks for a restart. The journal also records which staged restore it applied (the id stored in `restore-pending/ready`), so cleanup never deletes a different restore that was staged afterwards.

Do not delete `restore-journal.json`, `restore-previous` or `restore-pending` by hand while a journal exists, because together they are what lets the next start put matching files back. If startup reports that the journal cannot be read, stop the app, copy the whole data directory somewhere safe, and then look in `restore-previous` and `restore-pending`: the database and `keys` folder that belong together are either both in `restore-previous` (the old data) or both live in the data directory (the restored data).
