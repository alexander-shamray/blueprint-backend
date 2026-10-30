using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>The send claim's index, keyed on the row's id rather than its due time (ADR-175).</summary>
/// <remarks>
/// Additive for the version before (§7.4): the name, the filter and the columns it reads are kept, so that worker's
/// claim still finds every column in it, and this one's index hint names an index either schema has.
/// </remarks>
public partial class RekeySendClaimIndex : Migration
{
    // Fields, for CA1861.
    private static readonly string[] ClaimIncluded = ["NextAttemptAt", "LockedUntil", "Channel"];
    private static readonly string[] FormerClaimIncluded = ["LockedUntil", "Channel"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog",
            column: "NotificationId",
            filter: "[Status] = 'Pending'")
            .Annotation("SqlServer:Include", ClaimIncluded);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog",
            column: "NextAttemptAt",
            filter: "[Status] = 'Pending'")
            .Annotation("SqlServer:Include", FormerClaimIncluded);
    }
}
