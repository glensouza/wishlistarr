using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddLibraryBackfill : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LibraryBackfillBatches",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                KidProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                ServerId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Error = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibraryBackfillBatches", x => x.Id);
                table.ForeignKey(
                    name: "FK_LibraryBackfillBatches_KidProfiles_KidProfileId",
                    column: x => x.KidProfileId,
                    principalTable: "KidProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "LibraryBackfillItems",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                BatchId = table.Column<int>(type: "INTEGER", nullable: false),
                RatingKey = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Guid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                OwnsLabel = table.Column<bool>(type: "INTEGER", nullable: false),
                Detail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibraryBackfillItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_LibraryBackfillItems_LibraryBackfillBatches_BatchId",
                    column: x => x.BatchId,
                    principalTable: "LibraryBackfillBatches",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LibraryBackfillBatches_KidProfileId",
            table: "LibraryBackfillBatches",
            column: "KidProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_LibraryBackfillItems_BatchId_RatingKey",
            table: "LibraryBackfillItems",
            columns: ["BatchId", "RatingKey"],
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "LibraryBackfillItems");

        migrationBuilder.DropTable(
            name: "LibraryBackfillBatches");
    }
}
