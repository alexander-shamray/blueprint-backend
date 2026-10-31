using Microsoft.EntityFrameworkCore.Migrations;

namespace Web.Bff.Persistence.Migrations;

/// <summary>The owned-orders index includes every column the buyer's page reads, so its walk pays no lookup a row,
/// on ADR-179's argument for Catalog's; generated from its configuration.</summary>
public partial class CoverOwnedOrdersIndex : Migration
{
    private static readonly string[] Keys = ["CustomerId", "FirstSeenAt", "OrderId"];

    private static readonly bool[] Descending = [false, true, true];

    private static readonly string[] IncludedColumns =
    [
        "Currency", "TotalAmount", "PlacedAt", "ConfirmedAt", "DispatchedAt", "DeliveredAt", "CancelledAt",
        "CancelOutcome", "AuthorisedAt", "AuthorisedAmount", "RefundedAt", "RefundedAmount", "PaymentCurrency",
        "TrackingNumber", "AsOf"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Orders_Owned",
            schema: "bff",
            table: "Orders");

        migrationBuilder.CreateIndex(
            name: "IX_Orders_Owned",
            schema: "bff",
            table: "Orders",
            columns: Keys,
            descending: Descending,
            filter: "[CustomerId] IS NOT NULL")
            .Annotation("SqlServer:Include", IncludedColumns);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Orders_Owned",
            schema: "bff",
            table: "Orders");

        migrationBuilder.CreateIndex(
            name: "IX_Orders_Owned",
            schema: "bff",
            table: "Orders",
            columns: Keys,
            descending: Descending,
            filter: "[CustomerId] IS NOT NULL");
    }
}
