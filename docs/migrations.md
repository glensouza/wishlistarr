# Database migrations

Wishlistarr stores its data in a SQLite database inside the data directory (`WISHLISTARR_DATA_DIR`, default `data` under the application folder). The schema is managed with EF Core migrations in `src/Wishlistarr.Data/Migrations`, and pending migrations are applied automatically on startup.

## Policy

- Migrations never destroy user data. A migration must not drop a table or column, narrow a column type, or otherwise discard values that exist in a user's database.
- Removing or reshaping something is done in steps across releases: add the new structure, copy the data across, stop using the old structure. The old structure stays in the schema, unused.
- Every migration must apply cleanly to a database created by any earlier release.
- Migrations are append-only. A migration that has shipped in a release is never edited or deleted; fix problems with a new migration.
- Down methods are never run in production; rollbacks are done by restoring a backup or shipping a forward migration.
- When the database file already exists and a migration is pending, startup takes a backup first and does not apply the migration if that backup fails. A new data directory is migrated without a backup. See [backup.md](backup.md).
- Each migration is committed together with the model change that produced it, including the generated designer and model snapshot files.
- Backfills and data conversions live in the migration itself so that startup produces a consistent database.

## Adding a migration

```sh
dotnet ef migrations add <Name> --project src/Wishlistarr.Data
```

Review the generated `Up` method for any `DropTable`, `DropColumn`, `AlterColumn` or `DeleteData` operation before committing. Anything that would lose data must be rewritten to follow the policy above.

## Connection settings

Every connection runs `PRAGMA journal_mode=WAL` and a 5 second `busy_timeout`, configured in `SqliteConfiguration`.
