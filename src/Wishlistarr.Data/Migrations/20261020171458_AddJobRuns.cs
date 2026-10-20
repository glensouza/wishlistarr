using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddJobRuns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "JobRuns",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                JobName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Trigger = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JobRuns", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_JobRuns_JobName_Id",
            table: "JobRuns",
            columns: ["JobName", "Id"]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "JobRuns");
    }
}
