using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// The index §9.4's retention purge deletes through, generated from
/// <see cref="OutboxMessageConfiguration"/> — the configuration is the source
/// of truth and only this file's dress is hand-authored. The
/// <c>.Designer.cs</c> and the snapshot beside it are machine-owned and
/// untouched.
/// </summary>
/// <remarks>
/// <b>A second index rather than a wider first one, because the two predicates
/// are complements.</b> <c>IX_Outbox_Unprocessed</c> is filtered
/// <c>WHERE ProcessedAt IS NULL</c> for the claim, which excludes by
/// construction every row the purge's <c>ProcessedAt IS NOT NULL AND
/// ProcessedAt &lt; @Before</c> targets — so the purge had no usable index at
/// all and scanned the whole table, hourly, up to twenty times a pass. The
/// scan grew with the processed rows, which is to say it grew exactly as the
/// purge started to matter.
/// <para>
/// Filtered the other way for the same reason its twin is filtered: the purge
/// never reads an unprocessed row, so the index stays the size of the undeleted
/// backlog rather than of the table.
/// </para>
/// </remarks>
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
