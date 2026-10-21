using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddWebhookEvents : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WebhookEvents",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                DeliveryKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                EventType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ProcessedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebhookEvents", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WebhookEvents_ProcessedAt",
            table: "WebhookEvents",
            column: "ProcessedAt");

        migrationBuilder.CreateIndex(
            name: "IX_WebhookEvents_Source_DeliveryKey",
            table: "WebhookEvents",
            columns: ["Source", "DeliveryKey"],
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "WebhookEvents");
    }
}
