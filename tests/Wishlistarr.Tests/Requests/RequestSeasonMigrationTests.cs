using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Requests;

public sealed class RequestSeasonMigrationTests
{
    [Fact]
    public async Task Existing_show_access_is_monitored_without_turning_parent_grants_into_downloads()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<WishlistarrDbContext> options = new DbContextOptionsBuilder<WishlistarrDbContext>().UseSqlite(connection).Options;
        await using WishlistarrDbContext db = new(options);
        string previous = db.Database.GetMigrations().Last(name => !name.EndsWith("_AddRequestSeasons", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(previous);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO KidProfiles (Id, Name, Avatar, PlexLabel, CreatedAt, AutoApprove, FailedPinCount, RemoveOwnedLabelsOnArchive)
            VALUES (1, 'Kid fixture', 'fox', 'Kid fixture', '2026-10-01 00:00:00+00:00', 0, 0, 0);
            INSERT INTO MediaRequests (Id, KidProfileId, Kind, TmdbId, State, RequestCount, CountsTowardAllowance, CreatedAt, LastRequestedAt, StateChangedAt, Version)
            VALUES (1, 1, 'Show', 10, 'Labeled', 1, 0, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3),
                   (2, 1, 'Show', 20, 'Labeled', 1, 1, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3),
                   (3, 1, 'Movie', 30, 'Labeled', 1, 0, '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', '2026-10-01 00:00:00+00:00', 3);
            INSERT INTO MediaRequestEvents (MediaRequestId, ToState, ActorKind, ActorName, Reason, OccurredAt)
            VALUES (1, 'Pending', 'Parent', 'parent', 'Parent grant', '2026-10-01 00:00:00+00:00'),
                   (2, 'Pending', 'Kid', 'Kid fixture', NULL, '2026-10-01 00:00:00+00:00');
            """);
        await db.Database.MigrateAsync();

        MediaRequest[] requests = await db.MediaRequests.OrderBy(request => request.Id).ToArrayAsync();
        Assert.Equal([RequestState.Monitoring, RequestState.Monitoring, RequestState.Labeled], requests.Select(request => request.State));
        Assert.All(requests, request => Assert.True(request.AllSeasons));
        Assert.False(requests[0].DownloadRequested);
        Assert.True(requests[1].DownloadRequested);
        Assert.Equal([4, 4, 3], requests.Select(request => request.Version));
        Assert.Equal(2, await db.MediaRequestEvents.CountAsync(item => item.FromState == RequestState.Labeled && item.ToState == RequestState.Monitoring));
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
