using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AllowAnotherRequestAfterRevoke : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MediaRequests_KidProfileId_Kind_TmdbId",
            table: "MediaRequests");

        migrationBuilder.CreateIndex(
            name: "IX_MediaRequests_KidProfileId_Kind_TmdbId",
            table: "MediaRequests",
            columns: ["KidProfileId", "Kind", "TmdbId"],
            unique: true,
            filter: "\"State\" <> 'Denied' AND \"State\" <> 'Revoked'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MediaRequests_KidProfileId_Kind_TmdbId",
            table: "MediaRequests");

        migrationBuilder.CreateIndex(
            name: "IX_MediaRequests_KidProfileId_Kind_TmdbId",
            table: "MediaRequests",
            columns: ["KidProfileId", "Kind", "TmdbId"],
            unique: true,
            filter: "\"State\" <> 'Denied'");
    }
}
