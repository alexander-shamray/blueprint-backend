using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>Drops the inbox's index on the time a row was handled, which every insert appended to (ADR-186).</summary>
/// <remarks>
/// Backward compatible with the version before (§7.4): its purge names no index, so it runs on either schema, and the
/// clustered key it then scans carries the one column the purge filters on.
/// </remarks>
public partial class DropInboxHandledAtIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Inbox_HandledAt",
            schema: "catalog",
            table: "InboxMessages");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_Inbox_HandledAt",
            schema: "catalog",
            table: "InboxMessages",
            column: "HandledAt");
    }
}
