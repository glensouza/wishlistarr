using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddRequestDispatch : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Backend",
            table: "MediaRequests",
            type: "TEXT",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DispatchedAt",
            table: "MediaRequests",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ExternalId",
            table: "MediaRequests",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Backend",
            table: "MediaRequests");

        migrationBuilder.DropColumn(
            name: "DispatchedAt",
            table: "MediaRequests");

        migrationBuilder.DropColumn(
            name: "ExternalId",
            table: "MediaRequests");
    }
}
