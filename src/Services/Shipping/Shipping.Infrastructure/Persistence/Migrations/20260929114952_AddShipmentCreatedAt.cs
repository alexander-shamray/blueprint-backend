using Microsoft.EntityFrameworkCore.Migrations;

namespace Shipping.Infrastructure.Persistence.Migrations;

/// <summary>
/// <c>Shipments.CreatedAt</c>, the clock ADR-052's give-up age is measured
/// from, generated from <see cref="ShipmentConfiguration"/>, which argues the
/// default; only this file's dress is hand-authored, and the
/// <c>.Designer.cs</c> and snapshot are untouched.
/// </summary>
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
