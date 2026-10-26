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
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PlexAddedAtUnix",
            table: "LibraryItems");
    }
}
