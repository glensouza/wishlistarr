using Microsoft.Data.Sqlite;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Backup;

public static class SqliteSnapshot
{
    public static async Task VacuumIntoAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        if (File.Exists(destinationPath))
        {
            throw new InvalidOperationException("The backup destination already exists.");
        }

        await using SqliteConnection connection = await OpenAsync(sourcePath, readOnly: false, ct);
        await using SqliteCommand timeout = connection.CreateCommand();
        timeout.CommandText = $"PRAGMA busy_timeout={SqliteConfiguration.BusyTimeoutMilliseconds};";
        await timeout.ExecuteNonQueryAsync(ct);

        await using SqliteCommand vacuum = connection.CreateCommand();
        vacuum.CommandText = "VACUUM INTO @destination;";
        vacuum.Parameters.AddWithValue("@destination", destinationPath);
        await vacuum.ExecuteNonQueryAsync(ct);
    }

    public static async Task<bool> IntegrityOkAsync(string databasePath, CancellationToken ct)
    {
        try
        {
            await using SqliteConnection connection = await OpenAsync(databasePath, readOnly: true, ct);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            List<string> rows = [];
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(reader.GetString(0));
            }

            return rows.Count == 1 && rows[0] == "ok";
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public static async Task<IReadOnlyList<string>?> ReadMigrationsAsync(string databasePath, CancellationToken ct)
    {
        try
        {
            await using SqliteConnection connection = await OpenAsync(databasePath, readOnly: true, ct);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\";";
            List<string> ids = [];
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0))
                {
                    return null;
                }

                string id = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(id))
                {
                    return null;
                }

                ids.Add(id);
            }

            return ids.Count == 0 ? null : ids;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath, bool readOnly, CancellationToken ct)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        SqliteConnection connection = new(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
