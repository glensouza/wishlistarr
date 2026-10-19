using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddParentAuthentication : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ParentAccounts",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                FailedLoginCount = table.Column<int>(type: "INTEGER", nullable: false),
                LockoutEndsAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ParentAccounts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ParentSessions",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ParentAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UserAgent = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                RemoteAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ParentSessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_ParentSessions_ParentAccounts_ParentAccountId",
                    column: x => x.ParentAccountId,
                    principalTable: "ParentAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ParentAccounts_Username",
            table: "ParentAccounts",
            column: "Username",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ParentSessions_ParentAccountId",
            table: "ParentSessions",
            column: "ParentAccountId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ParentSessions");

        migrationBuilder.DropTable(
            name: "ParentAccounts");
    }
}
