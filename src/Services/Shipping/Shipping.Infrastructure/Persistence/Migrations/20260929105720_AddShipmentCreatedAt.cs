using Microsoft.EntityFrameworkCore.Migrations;

namespace Shipping.Infrastructure.Persistence.Migrations;

/// <summary>
/// <c>Shipments.CreatedAt</c>, the clock ADR-052's give-up age is measured
/// from. Generated, then dressed: a file-scoped namespace, this comment, and
/// existing rows stamped with the moment the migration ran rather than the
/// generated <c>DateTimeOffset.MinValue</c>, which is older than every give-up
/// age and would end each pending row on the next pass. The designer file and
/// the snapshot are machine-owned.
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
            nullable: true);

        migrationBuilder.Sql("UPDATE shipping.Shipments SET CreatedAt = SYSDATETIMEOFFSET();");

        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "CreatedAt",
            schema: "shipping",
            table: "Shipments",
            type: "datetimeoffset(7)",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "datetimeoffset(7)",
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CreatedAt",
            schema: "shipping",
            table: "Shipments");
    }
}
