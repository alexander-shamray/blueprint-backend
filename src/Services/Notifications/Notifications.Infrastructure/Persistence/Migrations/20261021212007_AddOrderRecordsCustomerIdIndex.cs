using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>An index on an order record's customer, so the erasure's read that fences an intake (ADR-097) seeks its
/// subject rather than holding every customer's records.</summary>
public partial class AddOrderRecordsCustomerIdIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_OrderRecords_CustomerId",
            schema: "notifications",
            table: "OrderRecords",
            column: "CustomerId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_OrderRecords_CustomerId",
            schema: "notifications",
            table: "OrderRecords");
    }
}
