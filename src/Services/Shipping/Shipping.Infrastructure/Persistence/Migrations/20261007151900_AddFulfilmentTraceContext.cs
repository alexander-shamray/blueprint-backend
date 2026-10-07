using Microsoft.EntityFrameworkCore.Migrations;

namespace Shipping.Infrastructure.Persistence.Migrations;

/// <summary>The fulfilment worker's restored trace (§9.4), nullable so the serving version writes rows without it (§7.4).</summary>
public partial class AddFulfilmentTraceContext : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "TraceParent",
            schema: "shipping",
            table: "Shipments",
            type: "varchar(55)",
            unicode: false,
            maxLength: 55,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TraceState",
            schema: "shipping",
            table: "Shipments",
            type: "varchar(512)",
            unicode: false,
            maxLength: 512,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TraceParent",
            schema: "shipping",
            table: "Shipments");

        migrationBuilder.DropColumn(
            name: "TraceState",
            schema: "shipping",
            table: "Shipments");
    }
}
