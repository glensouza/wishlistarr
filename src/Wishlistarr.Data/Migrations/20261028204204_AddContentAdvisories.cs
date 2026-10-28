using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddContentAdvisories : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BlockedDescriptors",
            table: "KidProfiles",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.AddColumn<string>(
            name: "BlockedTopics",
            table: "KidProfiles",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.CreateTable(
            name: "ParentNotes",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                Text = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ParentNotes", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ParentNotes_Kind_TmdbId",
            table: "ParentNotes",
            columns: ["Kind", "TmdbId"],
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ParentNotes");

        migrationBuilder.DropColumn(
            name: "BlockedDescriptors",
            table: "KidProfiles");

        migrationBuilder.DropColumn(
            name: "BlockedTopics",
            table: "KidProfiles");
    }
}
