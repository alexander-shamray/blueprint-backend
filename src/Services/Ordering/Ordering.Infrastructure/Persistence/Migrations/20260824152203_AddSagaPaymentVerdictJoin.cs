using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>The two facts §9.6's <c>Compensating</c> joins on, generated from the saga's configuration.</summary>
/// <remarks>NOT NULL with the conservative default 0, invisible to the release still serving (§9.6, §15.5).</remarks>
public partial class AddSagaPaymentVerdictJoin : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "PaymentVerdictOutstanding",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "StockReleaseSettled",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PaymentVerdictOutstanding",
            schema: "ordering",
            table: "OrderFulfilmentStates");

        migrationBuilder.DropColumn(
            name: "StockReleaseSettled",
            schema: "ordering",
            table: "OrderFulfilmentStates");
    }
}
