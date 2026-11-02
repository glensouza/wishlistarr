using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddRequestSeasons : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "AllSeasons",
            table: "MediaRequests",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "DownloadRequested",
            table: "MediaRequests",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.CreateTable(
            name: "MediaRequestSeason",
            columns: table => new
            {
                MediaRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                Number = table.Column<int>(type: "INTEGER", nullable: false),
                Requested = table.Column<bool>(type: "INTEGER", nullable: false),
                Availability = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                AvailableEpisodes = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MediaRequestSeason", x => new { x.MediaRequestId, x.Number });
                table.ForeignKey(
                    name: "FK_MediaRequestSeason_MediaRequests_MediaRequestId",
                    column: x => x.MediaRequestId,
                    principalTable: "MediaRequests",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.Sql("""
            UPDATE MediaRequests SET DownloadRequested = 0
            WHERE EXISTS (SELECT 1 FROM MediaRequestEvents e WHERE e.MediaRequestId = MediaRequests.Id
                AND e.FromState IS NULL AND e.ActorKind = 'Parent' AND e.Reason = 'Parent grant');
            UPDATE MediaRequests SET DownloadRequested = 0
            WHERE Kind = 'Show' AND State IN ('Labeled', 'Available') AND DispatchedAt IS NULL;
            UPDATE MediaRequests SET DownloadRequested = 0
            WHERE Kind = 'Show' AND State = 'Failed' AND DispatchedAt IS NULL
                AND (SELECT e.FromState FROM MediaRequestEvents e WHERE e.MediaRequestId = MediaRequests.Id
                    AND e.ToState = 'Failed' ORDER BY e.Id DESC LIMIT 1) = 'Available';
            INSERT INTO MediaRequestEvents (MediaRequestId, FromState, ToState, ActorKind, ActorName, OccurredAt)
            SELECT Id, 'Labeled', 'Monitoring', 'System', 'season-tracking', strftime('%Y-%m-%d %H:%M:%f+00:00', 'now')
            FROM MediaRequests WHERE Kind = 'Show' AND State = 'Labeled';
            UPDATE MediaRequests SET State = 'Monitoring', Version = Version + 1,
                StateChangedAt = strftime('%Y-%m-%d %H:%M:%f+00:00', 'now')
            WHERE Kind = 'Show' AND State = 'Labeled';
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE MediaRequestEvents SET ToState = 'Labeled' WHERE ToState = 'Monitoring' AND FromState = 'Available';
            UPDATE MediaRequestEvents SET FromState = 'Labeled' WHERE FromState = 'Monitoring' AND ToState = 'Revoked';
            DELETE FROM MediaRequestEvents WHERE FromState = 'Monitoring' OR ToState = 'Monitoring';
            UPDATE MediaRequests SET State = 'Labeled', Version = Version + 1 WHERE State = 'Monitoring';
            """);

        migrationBuilder.DropTable(
            name: "MediaRequestSeason");

        migrationBuilder.DropColumn(
            name: "AllSeasons",
            table: "MediaRequests");

        migrationBuilder.DropColumn(
            name: "DownloadRequested",
            table: "MediaRequests");
    }
}
