using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Wishlistarr.Data;

public static class SqliteConfiguration
{
    public const int BusyTimeoutMilliseconds = 5000;

    public static DbContextOptionsBuilder UseWishlistarrSqlite(this DbContextOptionsBuilder builder, string databasePath) =>
        builder
            .UseSqlite($"Data Source={databasePath}")
            .AddInterceptors(new PragmaInterceptor());

    private sealed class PragmaInterceptor : DbConnectionInterceptor
    {
        private static readonly string Pragmas =
            $"PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout={BusyTimeoutMilliseconds}; PRAGMA foreign_keys=ON;";

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
            Apply(connection);

        public override Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Apply(connection);
            return Task.CompletedTask;
        }

        private static void Apply(DbConnection connection)
        {
            using DbCommand command = connection.CreateCommand();
            command.CommandText = Pragmas;
            command.ExecuteNonQuery();
        }
    }
}
