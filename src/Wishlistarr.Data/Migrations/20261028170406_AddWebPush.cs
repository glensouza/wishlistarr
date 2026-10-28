using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wishlistarr.Data.Migrations;

/// <inheritdoc />
public partial class AddWebPush : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "KidReadyNotices",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                KidProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                MediaRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                DismissedAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KidReadyNotices", x => x.Id);
                table.ForeignKey(
                    name: "FK_KidReadyNotices_KidProfiles_KidProfileId",
                    column: x => x.KidProfileId,
                    principalTable: "KidProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_KidReadyNotices_MediaRequests_MediaRequestId",
                    column: x => x.MediaRequestId,
                    principalTable: "MediaRequests",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "WebPushSubscriptions",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ParentAccountId = table.Column<int>(type: "INTEGER", nullable: true),
                KidDeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                Endpoint = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                P256dh = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                Auth = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebPushSubscriptions", x => x.Id);
                table.CheckConstraint("CK_WebPushSubscription_Owner", "(\"ParentAccountId\" IS NULL) <> (\"KidDeviceId\" IS NULL)");
                table.ForeignKey(
                    name: "FK_WebPushSubscriptions_KidDevices_KidDeviceId",
                    column: x => x.KidDeviceId,
                    principalTable: "KidDevices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_WebPushSubscriptions_ParentAccounts_ParentAccountId",
                    column: x => x.ParentAccountId,
                    principalTable: "ParentAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "WebPushDeliveries",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                WebPushSubscriptionId = table.Column<int>(type: "INTEGER", nullable: false),
                MediaRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                KidProfileId = table.Column<int>(type: "INTEGER", nullable: true),
                Event = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebPushDeliveries", x => x.Id);
                table.ForeignKey(
                    name: "FK_WebPushDeliveries_WebPushSubscriptions_WebPushSubscriptionId",
                    column: x => x.WebPushSubscriptionId,
                    principalTable: "WebPushSubscriptions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_KidReadyNotices_KidProfileId_MediaRequestId",
            table: "KidReadyNotices",
            columns: ["KidProfileId", "MediaRequestId"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_KidReadyNotices_MediaRequestId",
            table: "KidReadyNotices",
            column: "MediaRequestId");

        migrationBuilder.CreateIndex(
            name: "IX_WebPushDeliveries_CompletedAt_Attempts_NextAttemptAt",
            table: "WebPushDeliveries",
            columns: ["CompletedAt", "Attempts", "NextAttemptAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_WebPushDeliveries_WebPushSubscriptionId_MediaRequestId_Event",
            table: "WebPushDeliveries",
            columns: ["WebPushSubscriptionId", "MediaRequestId", "Event"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_WebPushSubscriptions_Endpoint",
            table: "WebPushSubscriptions",
            column: "Endpoint",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_WebPushSubscriptions_KidDeviceId",
            table: "WebPushSubscriptions",
            column: "KidDeviceId");

        migrationBuilder.CreateIndex(
            name: "IX_WebPushSubscriptions_ParentAccountId",
            table: "WebPushSubscriptions",
            column: "ParentAccountId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "KidReadyNotices");

        migrationBuilder.DropTable(
            name: "WebPushDeliveries");

        migrationBuilder.DropTable(
            name: "WebPushSubscriptions");
    }
}
