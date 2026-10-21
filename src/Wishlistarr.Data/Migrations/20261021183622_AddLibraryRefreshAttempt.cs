using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddLibraryRefreshAttempt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastFullAttemptAt",
            table: "LibraryRefreshStates",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "LastFullAttemptComplete",
            table: "LibraryRefreshStates",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LastFullAttemptAt",
            table: "LibraryRefreshStates");

        migrationBuilder.DropColumn(
            name: "LastFullAttemptComplete",
            table: "LibraryRefreshStates");
    }
}
