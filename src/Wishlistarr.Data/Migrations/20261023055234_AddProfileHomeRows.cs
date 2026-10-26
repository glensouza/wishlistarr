using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddProfileHomeRows : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "HomeRows",
            table: "KidProfiles",
            type: "TEXT",
            nullable: false,
            defaultValue: "[\"popular\",\"library\",\"genre\"]");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "HomeRows",
            table: "KidProfiles");
    }
}
