using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddProfileContentRules : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AllowedOriginalLanguages",
            table: "KidProfiles",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.AddColumn<string>(
            name: "BlockedGenreIds",
            table: "KidProfiles",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.AddColumn<string>(
            name: "BlockedKeywordIds",
            table: "KidProfiles",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AllowedOriginalLanguages",
            table: "KidProfiles");

        migrationBuilder.DropColumn(
            name: "BlockedGenreIds",
            table: "KidProfiles");

        migrationBuilder.DropColumn(
            name: "BlockedKeywordIds",
            table: "KidProfiles");
    }
}
