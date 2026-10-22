using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddKidDevices : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "KidDeviceId",
            table: "KidSessions",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "DeviceHash",
            table: "KidPinAttempts",
            type: "TEXT",
            maxLength: 64,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 64);

        migrationBuilder.AddColumn<int>(
            name: "KidDeviceId",
            table: "KidPinAttempts",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LockedAt",
            table: "KidPinAttempts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "KidDevices",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                PairedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KidDevices", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "KidDeviceProfiles",
            columns: table => new
            {
                KidDeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                KidProfileId = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KidDeviceProfiles", x => new { x.KidDeviceId, x.KidProfileId });
                table.ForeignKey(
                    name: "FK_KidDeviceProfiles_KidDevices_KidDeviceId",
                    column: x => x.KidDeviceId,
                    principalTable: "KidDevices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_KidDeviceProfiles_KidProfiles_KidProfileId",
                    column: x => x.KidProfileId,
                    principalTable: "KidProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "KidPairingCodes",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                KidDeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KidPairingCodes", x => x.Id);
                table.ForeignKey(
                    name: "FK_KidPairingCodes_KidDevices_KidDeviceId",
                    column: x => x.KidDeviceId,
                    principalTable: "KidDevices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_KidSessions_KidDeviceId",
            table: "KidSessions",
            column: "KidDeviceId");

        migrationBuilder.CreateIndex(
            name: "IX_KidPinAttempts_KidDeviceId",
            table: "KidPinAttempts",
            column: "KidDeviceId");

        migrationBuilder.CreateIndex(
            name: "IX_KidPinAttempts_KidProfileId_KidDeviceId",
            table: "KidPinAttempts",
            columns: ["KidProfileId", "KidDeviceId"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_KidDeviceProfiles_KidProfileId",
            table: "KidDeviceProfiles",
            column: "KidProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_KidPairingCodes_KidDeviceId",
            table: "KidPairingCodes",
            column: "KidDeviceId",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_KidPinAttempts_KidDevices_KidDeviceId",
            table: "KidPinAttempts",
            column: "KidDeviceId",
            principalTable: "KidDevices",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_KidSessions_KidDevices_KidDeviceId",
            table: "KidSessions",
            column: "KidDeviceId",
            principalTable: "KidDevices",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_KidPinAttempts_KidDevices_KidDeviceId",
            table: "KidPinAttempts");

        migrationBuilder.DropForeignKey(
            name: "FK_KidSessions_KidDevices_KidDeviceId",
            table: "KidSessions");

        migrationBuilder.DropTable(
            name: "KidDeviceProfiles");

        migrationBuilder.DropTable(
            name: "KidPairingCodes");

        migrationBuilder.DropTable(
            name: "KidDevices");

        migrationBuilder.DropIndex(
            name: "IX_KidSessions_KidDeviceId",
            table: "KidSessions");

        migrationBuilder.DropIndex(
            name: "IX_KidPinAttempts_KidDeviceId",
            table: "KidPinAttempts");

        migrationBuilder.DropIndex(
            name: "IX_KidPinAttempts_KidProfileId_KidDeviceId",
            table: "KidPinAttempts");

        migrationBuilder.DropColumn(
            name: "KidDeviceId",
            table: "KidSessions");

        migrationBuilder.DropColumn(
            name: "KidDeviceId",
            table: "KidPinAttempts");

        migrationBuilder.DropColumn(
            name: "LockedAt",
            table: "KidPinAttempts");

        migrationBuilder.AlterColumn<string>(
            name: "DeviceHash",
            table: "KidPinAttempts",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 64,
            oldNullable: true);
    }
}
