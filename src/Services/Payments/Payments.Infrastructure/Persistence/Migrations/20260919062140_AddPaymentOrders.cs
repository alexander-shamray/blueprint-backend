using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// §3.2's order record, generated from <see cref="PaymentOrderRowConfiguration"/> —
/// the configuration is the source of truth and only this file's dress is
/// hand-authored (file-scoped namespace, this comment). The
/// <c>.Designer.cs</c> and the snapshot beside it are machine-owned and
/// untouched.
/// </summary>
public partial class AddPaymentOrders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PaymentOrders",
            schema: "payments",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                TotalAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                PlacedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PaymentOrders", x => x.OrderId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PaymentOrders",
            schema: "payments");
    }
}
