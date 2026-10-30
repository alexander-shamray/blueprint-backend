using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>The retention pass's index, keyed on the row's id rather than the instant it ended (ADR-176).</summary>
/// <remarks>
/// Backward compatible with the version before (§7.4): the name and the filter are kept and that version's purge names
/// no index, so its pass reads either schema, and the two included columns cover every column the purge asks of the row.
/// </remarks>
public partial class RekeyCompletedAtIndex : Migration
{
    // A field, for CA1861.
    private static readonly string[] PurgeIncluded = ["CompletedAt", "Status"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_CompletedAt",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_CompletedAt",
            schema: "notifications",
            table: "NotificationLog",
            column: "NotificationId",
            filter: "[CompletedAt] IS NOT NULL")
            .Annotation("SqlServer:Include", PurgeIncluded);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_CompletedAt",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_CompletedAt",
            schema: "notifications",
            table: "NotificationLog",
            column: "CompletedAt",
            filter: "[CompletedAt] IS NOT NULL");
    }
}
