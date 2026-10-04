using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Web.Bff.Persistence.Migrations;

/// <summary>
/// ADR-051's four tables, generated from the configurations: they are the source of truth, and the
/// <c>.Designer.cs</c> and snapshot beside it are machine-owned.
/// </summary>
public partial class AddOrderProjection : Migration
{
    // Fields, for CA1861.
    private static readonly string[] OwnedIndexColumns = ["CustomerId", "FirstSeenAt", "OrderId"];
    private static readonly bool[] OwnedIndexDescending = [false, true, true];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "bff");

        migrationBuilder.CreateTable(
            name: "InboxMessages",
            schema: "bff",
            columns: table => new
            {
                MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Endpoint = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false, collation: "Latin1_General_BIN2"),
                HandledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InboxMessages", x => new { x.MessageId, x.Endpoint });
            });

        migrationBuilder.CreateTable(
            name: "Orders",
            schema: "bff",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                TotalAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                PlacedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                DispatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                CancelOutcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                AuthorisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                AuthorisedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                RefundedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                RefundedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                PaymentCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                TrackingNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                AsOf = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Orders", x => x.OrderId);
                table.CheckConstraint("CK_Orders_Authorisation", "([AuthorisedAt] IS NULL AND [AuthorisedAmount] IS NULL) OR ([AuthorisedAt] IS NOT NULL AND [AuthorisedAmount] IS NOT NULL)");
                table.CheckConstraint("CK_Orders_Cancellation", "([CancelledAt] IS NULL AND [CancelOutcome] IS NULL) OR ([CancelledAt] IS NOT NULL AND [CancelOutcome] IS NOT NULL)");
                table.CheckConstraint("CK_Orders_CancelOutcome", "[CancelOutcome] IN (N'cancelled', N'out_of_stock', N'declined')");
                table.CheckConstraint("CK_Orders_PaymentCurrency", "([PaymentCurrency] IS NULL AND [AuthorisedAmount] IS NULL AND [RefundedAmount] IS NULL) OR ([PaymentCurrency] IS NOT NULL AND ([AuthorisedAmount] IS NOT NULL OR [RefundedAmount] IS NOT NULL))");
                table.CheckConstraint("CK_Orders_Refund", "([RefundedAt] IS NULL AND [RefundedAmount] IS NULL) OR ([RefundedAt] IS NOT NULL AND [RefundedAmount] IS NOT NULL)");
                table.CheckConstraint("CK_Orders_Total", "([Currency] IS NULL AND [TotalAmount] IS NULL) OR ([Currency] IS NOT NULL AND [TotalAmount] IS NOT NULL)");
            });

        migrationBuilder.CreateTable(
            name: "Products",
            schema: "bff",
            columns: table => new
            {
                ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Products", x => x.ProductId);
            });

        migrationBuilder.CreateTable(
            name: "OrderLines",
            schema: "bff",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                LineNumber = table.Column<int>(type: "int", nullable: false),
                ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Quantity = table.Column<int>(type: "int", nullable: false),
                UnitPrice = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderLines", x => new { x.OrderId, x.LineNumber });
                table.ForeignKey(
                    name: "FK_OrderLines_Orders_OrderId",
                    column: x => x.OrderId,
                    principalSchema: "bff",
                    principalTable: "Orders",
                    principalColumn: "OrderId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Inbox_HandledAt",
            schema: "bff",
            table: "InboxMessages",
            column: "HandledAt");

        migrationBuilder.CreateIndex(
            name: "IX_Orders_Owned",
            schema: "bff",
            table: "Orders",
            columns: OwnedIndexColumns,
            descending: OwnedIndexDescending,
            filter: "[CustomerId] IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "InboxMessages",
            schema: "bff");

        migrationBuilder.DropTable(
            name: "OrderLines",
            schema: "bff");

        migrationBuilder.DropTable(
            name: "Products",
            schema: "bff");

        migrationBuilder.DropTable(
            name: "Orders",
            schema: "bff");
    }
}
