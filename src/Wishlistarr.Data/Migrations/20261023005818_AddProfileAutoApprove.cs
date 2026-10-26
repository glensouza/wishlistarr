using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddProfileAutoApprove : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "AutoApprove",
            table: "KidProfiles",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "AutoApproveMovieRating",
            table: "KidProfiles",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AutoApproveTvRating",
            table: "KidProfiles",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AutoApprove",
            table: "KidProfiles");

        migrationBuilder.DropColumn(
            name: "AutoApproveMovieRating",
            table: "KidProfiles");

        migrationBuilder.DropColumn(
            name: "AutoApproveTvRating",
            table: "KidProfiles");
    }
}
