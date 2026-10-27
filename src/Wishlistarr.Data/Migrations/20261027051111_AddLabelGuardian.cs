using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddLabelGuardian : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LabelGuardianReports",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                ServerId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                LedgerChecked = table.Column<int>(type: "INTEGER", nullable: false),
                ExtrasChecked = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LabelGuardianReports", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "LabelGuardianEntries",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ReportId = table.Column<int>(type: "INTEGER", nullable: false),
                KidProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Action = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Guid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                RatingKey = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                PreviousRatingKey = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                PreviousGuid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                Detail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LabelGuardianEntries", x => x.Id);
                table.ForeignKey(
                    name: "FK_LabelGuardianEntries_KidProfiles_KidProfileId",
                    column: x => x.KidProfileId,
                    principalTable: "KidProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_LabelGuardianEntries_LabelGuardianReports_ReportId",
                    column: x => x.ReportId,
                    principalTable: "LabelGuardianReports",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LabelGuardianEntries_KidProfileId",
            table: "LabelGuardianEntries",
            column: "KidProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_LabelGuardianEntries_ReportId_Id",
            table: "LabelGuardianEntries",
            columns: ["ReportId", "Id"]);

        migrationBuilder.CreateIndex(
            name: "IX_LabelGuardianReports_StartedAt",
            table: "LabelGuardianReports",
            column: "StartedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "LabelGuardianEntries");

        migrationBuilder.DropTable(
            name: "LabelGuardianReports");
    }
}
