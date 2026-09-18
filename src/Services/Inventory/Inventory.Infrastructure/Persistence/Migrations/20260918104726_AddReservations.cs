using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Persistence.Migrations;

/// <summary>
/// §5's second aggregate, generated from <see cref="ReservationConfiguration"/>
/// — the configuration is the source of truth and only this file's dress is
/// hand-authored (file-scoped namespace, this comment). The <c>.Designer.cs</c>
/// and the snapshot beside it are machine-owned and untouched.
/// </summary>
public partial class AddReservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Reservations",
            schema: "inventory",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                UnavailableProductIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Reservations", x => x.OrderId);
            });

        migrationBuilder.CreateTable(
            name: "ReservationLines",
            schema: "inventory",
            columns: table => new
            {
                ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Quantity = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReservationLines", x => new { x.OrderId, x.ProductId });
                table.ForeignKey(
                    name: "FK_ReservationLines_Reservations_OrderId",
                    column: x => x.OrderId,
                    principalSchema: "inventory",
                    principalTable: "Reservations",
                    principalColumn: "OrderId",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ReservationLines",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "Reservations",
            schema: "inventory");
    }
}
