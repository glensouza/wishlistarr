using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;
/// <inheritdoc />
public partial class AddProfileTitleRules : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProfileTitleRules",
            columns: table => new
            {
                KidProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                Blocked = table.Column<bool>(type: "INTEGER", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true, collation: "NOCASE"),
                TitleFetchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProfileTitleRules", x => new { x.KidProfileId, x.Kind, x.TmdbId, x.Blocked });
                table.ForeignKey(
                    name: "FK_ProfileTitleRules_KidProfiles_KidProfileId",
                    column: x => x.KidProfileId,
                    principalTable: "KidProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ProfileTitleRules");
    }
}

