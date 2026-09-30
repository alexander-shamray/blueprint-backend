using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>The fact §9.6's early-release doors record, generated from the saga's configuration.</summary>
/// <remarks>NOT NULL with default 0, "no cancellation seen", true of every older row (§9.6, §15.5).</remarks>
public partial class AddSagaCancellationObserved : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "CancellationObserved",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CancellationObserved",
            schema: "ordering",
            table: "OrderFulfilmentStates");
    }
}
