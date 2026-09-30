using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>The index §9.4's purge deletes through, generated from <see cref="OutboxMessageConfiguration"/>.</summary>
public partial class AddOutboxRetentionIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_Outbox_Processed",
            schema: "ordering",
            table: "OutboxMessages",
            column: "ProcessedAt",
            filter: "[ProcessedAt] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Outbox_Processed",
            schema: "ordering",
            table: "OutboxMessages");
    }
}
