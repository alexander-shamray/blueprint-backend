using Microsoft.EntityFrameworkCore.Migrations;

namespace Shipping.Infrastructure.Persistence.Migrations;

/// <summary>A shipment keeps the erasure requests its order's customer was already under when it was placed (ADR-103).</summary>
public partial class AddShipmentErasedUnder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ErasedUnder",
            schema: "shipping",
            table: "Shipments",
            type: "nvarchar(max)",
            nullable: false,
            defaultValueSql: "N'[]'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ErasedUnder",
            schema: "shipping",
            table: "Shipments");
    }
}
