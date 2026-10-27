using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddNotifications : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "NotificationApprovals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ParentAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                MediaRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                UsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationApprovals", x => x.Id);
                table.ForeignKey(
                    name: "FK_NotificationApprovals_MediaRequests_MediaRequestId",
                    column: x => x.MediaRequestId,
                    principalTable: "MediaRequests",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_NotificationApprovals_ParentAccounts_ParentAccountId",
                    column: x => x.ParentAccountId,
                    principalTable: "ParentAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "NotificationSubscriptions",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ParentAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                Events = table.Column<string>(type: "TEXT", nullable: false),
                ProtectedConfiguration = table.Column<string>(type: "TEXT", nullable: false),
                LastEventId = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationSubscriptions", x => x.Id);
                table.ForeignKey(
                    name: "FK_NotificationSubscriptions_ParentAccounts_ParentAccountId",
                    column: x => x.ParentAccountId,
                    principalTable: "ParentAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "NotificationDeliveries",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                NotificationSubscriptionId = table.Column<int>(type: "INTEGER", nullable: false),
                MediaRequestEventId = table.Column<int>(type: "INTEGER", nullable: true),
                MediaRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                Event = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                table.ForeignKey(
                    name: "FK_NotificationDeliveries_NotificationSubscriptions_NotificationSubscriptionId",
                    column: x => x.NotificationSubscriptionId,
                    principalTable: "NotificationSubscriptions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_NotificationApprovals_MediaRequestId",
            table: "NotificationApprovals",
            column: "MediaRequestId");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationApprovals_ParentAccountId_MediaRequestId",
            table: "NotificationApprovals",
            columns: ["ParentAccountId", "MediaRequestId"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_NotificationDeliveries_CompletedAt",
            table: "NotificationDeliveries",
            column: "CompletedAt");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationDeliveries_NotificationSubscriptionId_MediaRequestEventId",
            table: "NotificationDeliveries",
            columns: ["NotificationSubscriptionId", "MediaRequestEventId"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_NotificationSubscriptions_ParentAccountId_Provider",
            table: "NotificationSubscriptions",
            columns: ["ParentAccountId", "Provider"],
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "NotificationApprovals");

        migrationBuilder.DropTable(
            name: "NotificationDeliveries");

        migrationBuilder.DropTable(
            name: "NotificationSubscriptions");
    }
}
