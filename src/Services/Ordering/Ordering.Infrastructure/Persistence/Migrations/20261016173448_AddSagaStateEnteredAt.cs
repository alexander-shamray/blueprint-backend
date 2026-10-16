using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>The saga keeps when each instance entered its state; NULL on one an older release wrote (§9.6).</summary>
public partial class AddSagaStateEnteredAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "StateEnteredAt",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "datetimeoffset(7)",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "StateEnteredAt",
            schema: "ordering",
            table: "OrderFulfilmentStates");
    }
}
