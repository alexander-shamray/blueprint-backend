using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// §3.2's <c>PaymentIntent</c>, generated from
/// <see cref="PaymentIntentConfiguration"/> — the configuration is the
/// source of truth and only this file's dress is hand-authored (file-scoped
/// namespace, this comment). The <c>.Designer.cs</c> and the snapshot beside
/// it are machine-owned and untouched.
/// </summary>
public partial class AddPaymentIntents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PaymentIntents",
            schema: "payments",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                DeclineReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PaymentIntents", x => x.OrderId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PaymentIntents",
            schema: "payments");
    }
}
