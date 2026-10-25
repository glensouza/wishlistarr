using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddLibraryAddedAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "PlexAddedAtUnix",
            table: "LibraryItems",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_LibraryItems_ServerId_PlexAddedAtUnix",
            table: "LibraryItems",
            columns: ["ServerId", "PlexAddedAtUnix"]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_LibraryItems_ServerId_PlexAddedAtUnix",
            table: "LibraryItems");

        migrationBuilder.DropColumn(
            name: "PlexAddedAtUnix",
            table: "LibraryItems");
    }
}
