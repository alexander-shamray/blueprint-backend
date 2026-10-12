using Microsoft.EntityFrameworkCore.Migrations;

namespace Web.Bff.Persistence.Migrations;

/// <summary>The ids of orders deleted for an erased customer, and the time each was (ADR-098).</summary>
public partial class AddErasedOrders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ErasedOrders",
            schema: "bff",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ErasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ErasedOrders", x => x.OrderId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ErasedOrders",
            schema: "bff");
    }
}
