using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>The send claim's and the retention pass's indexes, generated from the three configurations.</summary>
public partial class AddSendAndRetentionIndexes : Migration
{
    // A field, for CA1861.
    private static readonly string[] ClaimIncluded = ["LockedUntil"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_OrderRecords_RecordedAt",
            schema: "notifications",
            table: "OrderRecords",
            column: "RecordedAt");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_CompletedAt",
            schema: "notifications",
            table: "NotificationLog",
            column: "CompletedAt",
            filter: "[CompletedAt] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_PendingOrder",
            schema: "notifications",
            table: "NotificationLog",
            column: "OrderId",
            filter: "[Status] = 'Pending'");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog",
            column: "NextAttemptAt",
            filter: "[Status] = 'Pending'")
            .Annotation("SqlServer:Include", ClaimIncluded);

        migrationBuilder.CreateIndex(
            name: "IX_ContactRecords_FetchedAt",
            schema: "notifications",
            table: "ContactRecords",
            column: "FetchedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_OrderRecords_RecordedAt",
            schema: "notifications",
            table: "OrderRecords");

        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_CompletedAt",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_PendingOrder",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.DropIndex(
            name: "IX_ContactRecords_FetchedAt",
            schema: "notifications",
            table: "ContactRecords");
    }
}
