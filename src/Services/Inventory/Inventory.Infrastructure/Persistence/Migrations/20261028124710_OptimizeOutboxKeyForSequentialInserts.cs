using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Persistence.Migrations;

/// <summary>
/// Paces the inserts queueing on the identity key's last-page latch (ADR-161). EF Core has no
/// builder for the option, so this is SQL and the model snapshot does not record it.
/// </summary>
public partial class OptimizeOutboxKeyForSequentialInserts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER INDEX PK_OutboxMessages ON inventory.OutboxMessages
            SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER INDEX PK_OutboxMessages ON inventory.OutboxMessages
            SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF);
            """);
    }
}
