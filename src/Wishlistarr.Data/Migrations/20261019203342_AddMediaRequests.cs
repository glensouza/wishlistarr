using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddMediaRequests : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MediaRequests",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                KidProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                RequestCount = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                LastRequestedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                StateChangedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Version = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MediaRequests", x => x.Id);
                table.ForeignKey(
                    name: "FK_MediaRequests_KidProfiles_KidProfileId",
                    column: x => x.KidProfileId,
                    principalTable: "KidProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "MediaRequestEvents",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                MediaRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                FromState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                ToState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                ActorKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                ActorId = table.Column<int>(type: "INTEGER", nullable: true),
                ActorName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MediaRequestEvents", x => x.Id);
                table.ForeignKey(
                    name: "FK_MediaRequestEvents_MediaRequests_MediaRequestId",
                    column: x => x.MediaRequestId,
                    principalTable: "MediaRequests",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MediaRequestEvents_MediaRequestId",
            table: "MediaRequestEvents",
            column: "MediaRequestId");

        migrationBuilder.CreateIndex(
            name: "IX_MediaRequests_KidProfileId_Kind_TmdbId",
            table: "MediaRequests",
            columns: ["KidProfileId", "Kind", "TmdbId"],
            unique: true,
            filter: "\"State\" <> 'Denied'");

        migrationBuilder.CreateIndex(
            name: "IX_MediaRequests_State",
            table: "MediaRequests",
            column: "State");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MediaRequestEvents");

        migrationBuilder.DropTable(
            name: "MediaRequests");
    }
}
