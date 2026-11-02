using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Requests;

public sealed class RequestSeasonMigrationTests
{
    private const string Migration = "_AddRequestSeasons";

    [Fact]
    public async Task Existing_show_access_is_monitored_without_starting_downloads_nobody_sent()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<WishlistarrDbContext> options = new DbContextOptionsBuilder<WishlistarrDbContext>().UseSqlite(connection).Options;
        await using WishlistarrDbContext db = new(options);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db));
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO KidProfiles (Id, Name, Avatar, PlexLabel, CreatedAt, AutoApprove, FailedPinCount, RemoveOwnedLabelsOnArchive)
            VALUES (1, 'Kid fixture', 'fox', 'Kid fixture', '2026-10-01 00:00:00+00:00', 0, 0, 0);
            INSERT INTO MediaRequests (Id, KidProfileId, Kind, TmdbId, State, RequestCount, CountsTowardAllowance, CreatedAt, LastRequestedAt, StateChangedAt, Version, DispatchedAt)
            VALUES (1, 1, 'Show', 10, 'Labeled', 1, 0, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3, '2026-10-01 00:00:00+00:00'),
                   (2, 1, 'Show', 20, 'Labeled', 1, 1, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3, '2026-10-01 00:00:00+00:00'),
                   (3, 1, 'Movie', 30, 'Labeled', 1, 0, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3, NULL),
                   (4, 1, 'Show', 40, 'Labeled', 1, 0, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3, NULL),
                   (5, 1, 'Show', 50, 'Available', 1, 0, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3, NULL),
                   (6, 1, 'Show', 60, 'Fulfilling', 1, 1, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3, NULL),
                   (7, 1, 'Show', 70, 'Failed', 1, 0, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3, NULL),
                   (8, 1, 'Show', 80, 'Failed', 1, 0, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3, NULL);
            INSERT INTO MediaRequestEvents (MediaRequestId, FromState, ToState, ActorKind, ActorName, Reason, OccurredAt)
            VALUES (1, NULL, 'Pending', 'Parent', 'parent', 'Parent grant', '2026-10-01 00:00:00+00:00'),
                   (2, NULL, 'Pending', 'Kid', 'Kid fixture', NULL, '2026-10-01 00:00:00+00:00'),
                   (7, 'Available', 'Failed', 'System', 'instant-label', NULL, '2026-10-01 00:00:00+00:00'),
                   (8, 'Fulfilling', 'Failed', 'System', 'dispatch', NULL, '2026-10-01 00:00:00+00:00');
            """);
        await db.Database.MigrateAsync();

        MediaRequest[] requests = await db.MediaRequests.OrderBy(request => request.Id).ToArrayAsync();
        Assert.Equal(
            [RequestState.Monitoring, RequestState.Monitoring, RequestState.Labeled, RequestState.Monitoring, RequestState.Available, RequestState.Fulfilling, RequestState.Failed, RequestState.Failed],
            requests.Select(request => request.State));
        Assert.All(requests, request => Assert.True(request.AllSeasons));
        Assert.Equal([false, true, true, false, false, true, false, true], requests.Select(request => request.DownloadRequested));
        Assert.Equal([4, 4, 3, 4, 3, 3, 3, 3], requests.Select(request => request.Version));
        Assert.Equal([true, true, false, false, false, false, false, false], requests.Select(request => request.AcceptedAt is not null));
        Assert.Equal(3, await db.MediaRequestEvents.CountAsync(item => item.FromState == RequestState.Labeled && item.ToState == RequestState.Monitoring));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Rolling_back_moves_monitored_shows_to_labeled_and_removes_the_monitoring_events()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<WishlistarrDbContext> options = new DbContextOptionsBuilder<WishlistarrDbContext>().UseSqlite(connection).Options;
        await using WishlistarrDbContext db = new(options);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO KidProfiles (Id, Name, Avatar, PlexLabel, CreatedAt, AutoApprove, FailedPinCount, RemoveOwnedLabelsOnArchive)
            VALUES (1, 'Kid fixture', 'fox', 'Kid fixture', '2026-10-01 00:00:00+00:00', 0, 0, 0);
            INSERT INTO MediaRequests (Id, KidProfileId, Kind, TmdbId, State, RequestCount, CountsTowardAllowance, CreatedAt, LastRequestedAt, StateChangedAt, Version, AllSeasons, DownloadRequested)
            VALUES (1, 1, 'Show', 10, 'Monitoring', 1, 0, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3, 1, 0),
                   (2, 1, 'Show', 20, 'Revoked', 1, 0, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3, 1, 0);
            INSERT INTO MediaRequestEvents (MediaRequestId, FromState, ToState, ActorKind, ActorName, OccurredAt)
            VALUES (1, 'Approved', 'Available', 'System', 'dispatch', '2026-10-01 00:00:00+00:00'),
                   (1, 'Available', 'Monitoring', 'System', 'label', '2026-10-01 00:00:00+00:00'),
                   (1, 'Labeled', 'Monitoring', 'System', 'season-request', '2026-10-01 00:00:00+00:00'),
                   (1, 'Monitoring', 'Labeled', 'System', 'arrival', '2026-10-01 00:00:00+00:00'),
                   (2, 'Monitoring', 'Revoked', 'Parent', 'parent', '2026-10-01 00:00:00+00:00');
            """);

        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration(db));

        List<(string State, long Version)> states = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT State, Version FROM MediaRequests ORDER BY Id";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                states.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        Assert.Equal([("Labeled", 4L), ("Revoked", 3L)], states);
        List<(string From, string To)> events = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT FromState, ToState FROM MediaRequestEvents ORDER BY Id";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                events.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.Equal([("Approved", "Available"), ("Available", "Labeled"), ("Labeled", "Revoked")], events);
    }

    private static string PreviousMigration(WishlistarrDbContext db) =>
        db.Database.GetMigrations().Last(name => !name.EndsWith(Migration, StringComparison.Ordinal));
}
