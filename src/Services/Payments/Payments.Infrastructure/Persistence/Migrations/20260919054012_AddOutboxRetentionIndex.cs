using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// The index §9.4's retention purge deletes through, generated from
/// <see cref="OutboxMessageConfiguration"/> — the configuration is the
/// source of truth, and only this file's dress is hand-authored; the
/// <c>.Designer.cs</c> and snapshot beside it are untouched. A second index
/// exists because the two predicates are complements:
/// <c>IX_Outbox_Unprocessed</c> excludes by construction every row the
/// purge targets, and this one is filtered the other way for the same
/// reason — sized to the backlog, not the table.
/// </summary>
public partial class AddOutboxRetentionIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_Outbox_Processed",
            schema: "payments",
            table: "OutboxMessages",
            column: "ProcessedAt",
            filter: "[ProcessedAt] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Outbox_Processed",
            schema: "payments",
            table: "OutboxMessages");
    }
}
