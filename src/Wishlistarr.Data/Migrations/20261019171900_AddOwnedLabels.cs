using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddOwnedLabels : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OwnedLabels",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ServerId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Guid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                RatingKey = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                KidProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                AppliedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OwnedLabels", x => x.Id);
                table.ForeignKey(
                    name: "FK_OwnedLabels_KidProfiles_KidProfileId",
                    column: x => x.KidProfileId,
                    principalTable: "KidProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OwnedLabels_KidProfileId",
            table: "OwnedLabels",
            column: "KidProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_OwnedLabels_ServerId_Guid_Label",
            table: "OwnedLabels",
            columns: ["ServerId", "Guid", "Label"],
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OwnedLabels");
    }
}
