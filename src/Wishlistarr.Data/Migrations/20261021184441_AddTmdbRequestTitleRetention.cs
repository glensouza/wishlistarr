using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddTmdbRequestTitleRetention : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Title",
            table: "MediaRequests",
            type: "TEXT",
            maxLength: 300,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 300);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "TitleFetchedAt",
            table: "MediaRequests",
            type: "TEXT",
            nullable: true);

        migrationBuilder.Sql("UPDATE MediaRequests SET TitleFetchedAt = CreatedAt WHERE Title IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TitleFetchedAt",
            table: "MediaRequests");

        migrationBuilder.AlterColumn<string>(
            name: "Title",
            table: "MediaRequests",
            type: "TEXT",
            maxLength: 300,
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 300,
            oldNullable: true);
    }
}
