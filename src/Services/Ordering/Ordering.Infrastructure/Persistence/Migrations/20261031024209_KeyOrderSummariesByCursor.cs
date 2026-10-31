using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>The summaries' customer index is keyed in the history page's whole order, OrderId descending too, so a
/// cursor's page walks it with no sort (§6.6); generated from its configuration.</summary>
public partial class KeyOrderSummariesByCursor : Migration
{
    private const string Index = "IX_OrderSummaries_Customer_PlacedAt";

    private static readonly string[] CursorKey = ["CustomerId", "PlacedAt", "OrderId"];

    private static readonly bool[] CursorDescending = [false, true, true];

    private static readonly string[] PreviousKey = ["CustomerId", "PlacedAt"];

    private static readonly bool[] PreviousDescending = [false, true];

    private static readonly string[] IncludedColumns = ["Status", "TotalAmount", "Currency", "LineCount"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: Index, schema: "ordering", table: "OrderSummaries");

        migrationBuilder.CreateIndex(
            name: Index,
            schema: "ordering",
            table: "OrderSummaries",
            columns: CursorKey,
            descending: CursorDescending)
            .Annotation("SqlServer:Include", IncludedColumns);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: Index, schema: "ordering", table: "OrderSummaries");

        migrationBuilder.CreateIndex(
            name: Index,
            schema: "ordering",
            table: "OrderSummaries",
            columns: PreviousKey,
            descending: PreviousDescending)
            .Annotation("SqlServer:Include", IncludedColumns);
    }
}
