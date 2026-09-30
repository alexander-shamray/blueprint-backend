using Microsoft.EntityFrameworkCore.Migrations;

namespace Shipping.Infrastructure.Persistence.Migrations;

/// <summary>ADR-052's give-up clock, generated from <see cref="ShipmentConfiguration"/>.</summary>
public partial class AddShipmentCreatedAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "CreatedAt",
            schema: "shipping",
            table: "Shipments",
            type: "datetimeoffset(7)",
            nullable: false,
            defaultValueSql: "SYSDATETIMEOFFSET()");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CreatedAt",
            schema: "shipping",
            table: "Shipments");
    }
}
