using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>An order record keeps the erasure requests its customer was already under when it was placed (ADR-099).</summary>
public partial class AddPaymentOrderErasedUnder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ErasedUnder",
            schema: "payments",
            table: "PaymentOrders",
            type: "nvarchar(max)",
            nullable: false,
            defaultValueSql: "N'[]'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ErasedUnder",
            schema: "payments",
            table: "PaymentOrders");
    }
}
