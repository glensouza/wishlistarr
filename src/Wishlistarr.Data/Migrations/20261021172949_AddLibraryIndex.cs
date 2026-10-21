using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddLibraryIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LibraryRefreshStates",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                ServerId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                LastFullRefreshAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastIncrementalRefreshAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                UpdatedCursor = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibraryRefreshStates", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "LibraryTitles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Year = table.Column<int>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibraryTitles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "LibraryItems",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ServerId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                RatingKey = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Guid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                LibraryId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Year = table.Column<int>(type: "INTEGER", nullable: true),
                PlexUpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                IndexedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                TitleId = table.Column<int>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibraryItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_LibraryItems_LibraryTitles_TitleId",
                    column: x => x.TitleId,
                    principalTable: "LibraryTitles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "LibraryExternalIds",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Value = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Origin = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                FetchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibraryExternalIds", x => x.Id);
                table.ForeignKey(
                    name: "FK_LibraryExternalIds_LibraryItems_ItemId",
                    column: x => x.ItemId,
                    principalTable: "LibraryItems",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "LibraryItemLabels",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibraryItemLabels", x => x.Id);
                table.ForeignKey(
                    name: "FK_LibraryItemLabels_LibraryItems_ItemId",
                    column: x => x.ItemId,
                    principalTable: "LibraryItems",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LibraryExternalIds_ItemId_Source_Value",
            table: "LibraryExternalIds",
            columns: ["ItemId", "Source", "Value"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LibraryExternalIds_Source_Value",
            table: "LibraryExternalIds",
            columns: ["Source", "Value"]);

        migrationBuilder.CreateIndex(
            name: "IX_LibraryItemLabels_ItemId_Label",
            table: "LibraryItemLabels",
            columns: ["ItemId", "Label"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LibraryItemLabels_Label",
            table: "LibraryItemLabels",
            column: "Label");

        migrationBuilder.CreateIndex(
            name: "IX_LibraryItems_ServerId_RatingKey",
            table: "LibraryItems",
            columns: ["ServerId", "RatingKey"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LibraryItems_TitleId",
            table: "LibraryItems",
            column: "TitleId");

        migrationBuilder.CreateIndex(
            name: "IX_LibraryTitles_Kind_TmdbId",
            table: "LibraryTitles",
            columns: ["Kind", "TmdbId"],
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "LibraryExternalIds");

        migrationBuilder.DropTable(
            name: "LibraryItemLabels");

        migrationBuilder.DropTable(
            name: "LibraryRefreshStates");

        migrationBuilder.DropTable(
            name: "LibraryItems");

        migrationBuilder.DropTable(
            name: "LibraryTitles");
    }
}
