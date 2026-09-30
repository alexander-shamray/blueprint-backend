using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>§9.6's saga store and review queue, generated from their two configurations.</summary>
public partial class AddFulfilmentSaga : Migration
{
    // A field, for CA1861: Annotation takes an object, so a collection expression has no target type there.
    private static readonly string[] IncludedColumns = ["CurrentState"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OrderFulfilmentStates",
            schema: "ordering",
            columns: table => new
            {
                CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CurrentState = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                CancelReason = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                StockTimeoutTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PaymentTimeoutTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DespatchTimeoutTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ReleaseTimeoutTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderFulfilmentStates", x => x.CorrelationId);
            });

        migrationBuilder.CreateTable(
            name: "OrderReviews",
            schema: "ordering",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Reason = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                RaisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderReviews", x => new { x.OrderId, x.Reason });
            });

        migrationBuilder.CreateIndex(
            name: "IX_OrderFulfilmentStates_StartedAt",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            column: "StartedAt")
            .Annotation("SqlServer:Include", IncludedColumns);

        migrationBuilder.CreateIndex(
            name: "IX_OrderReviews_RaisedAt",
            schema: "ordering",
            table: "OrderReviews",
            column: "RaisedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OrderFulfilmentStates",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "OrderReviews",
            schema: "ordering");
    }
}
