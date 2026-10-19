using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddKidProfiles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "KidProfiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "NOCASE"),
                Avatar = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                PinHash = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                MovieAgeLimit = table.Column<int>(type: "INTEGER", nullable: true),
                TvAgeLimit = table.Column<int>(type: "INTEGER", nullable: true),
                PlexLabel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                PlexUserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                WeeklyAllowance = table.Column<int>(type: "INTEGER", nullable: true),
                FailedPinCount = table.Column<int>(type: "INTEGER", nullable: false),
                LastFailedPinAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LockedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ArchivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                RemoveOwnedLabelsOnArchive = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KidProfiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "KidPinAttempts",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                KidProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                DeviceHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                FailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                LastFailedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KidPinAttempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_KidPinAttempts_KidProfiles_KidProfileId",
                    column: x => x.KidProfileId,
                    principalTable: "KidProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "KidSessions",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                KidProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UserAgent = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                RemoteAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KidSessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_KidSessions_KidProfiles_KidProfileId",
                    column: x => x.KidProfileId,
                    principalTable: "KidProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_KidPinAttempts_KidProfileId_DeviceHash",
            table: "KidPinAttempts",
            columns: ["KidProfileId", "DeviceHash"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_KidProfiles_Name",
            table: "KidProfiles",
            column: "Name",
            unique: true,
            filter: "\"ArchivedAt\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_KidProfiles_PlexLabel",
            table: "KidProfiles",
            column: "PlexLabel",
            unique: true,
            filter: "\"ArchivedAt\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_KidSessions_KidProfileId",
            table: "KidSessions",
            column: "KidProfileId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "KidPinAttempts");

        migrationBuilder.DropTable(
            name: "KidSessions");

        migrationBuilder.DropTable(
            name: "KidProfiles");
    }
}
