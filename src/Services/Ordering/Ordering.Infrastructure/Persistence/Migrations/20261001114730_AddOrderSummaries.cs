using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>§6.6's order summary table, generated from <see cref="OrderSummaryConfiguration"/>.</summary>
public partial class AddOrderSummaries : Migration
{
    // Fields, for CA1861: Annotation takes an object, so a collection expression has no target type there.
    private static readonly string[] IndexColumns = ["CustomerId", "PlacedAt"];
    private static readonly bool[] IndexDescending = [false, true];
    private static readonly string[] IncludedColumns = ["Status", "TotalAmount", "Currency", "LineCount"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OrderSummaries",
            schema: "ordering",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                TotalAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                Currency = table.Column<string>(
                    type: "char(3)",
                    unicode: false,
                    fixedLength: true,
                    maxLength: 3,
                    nullable: true),
                LineCount = table.Column<int>(type: "int", nullable: true),
                Products = table.Column<string>(type: "nvarchar(max)", nullable: true),
                PlacedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                CancelReason = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                PlacedCounted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                CancelledCounted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                FulfilmentCounted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderSummaries", x => x.OrderId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OrderSummaries_Customer_PlacedAt",
            schema: "ordering",
            table: "OrderSummaries",
            columns: IndexColumns,
            descending: IndexDescending)
            .Annotation("SqlServer:Include", IncludedColumns);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OrderSummaries",
            schema: "ordering");
    }
}
