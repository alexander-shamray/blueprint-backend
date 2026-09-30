using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>The token for §9.6's confirmation wait, generated from the saga's configuration.</summary>
/// <remarks>Nullable, so the release still serving never names it (§15.5); parked instances need none.</remarks>
public partial class AddSagaConfirmationTimeout : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ConfirmationTimeoutTokenId",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "uniqueidentifier",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ConfirmationTimeoutTokenId",
            schema: "ordering",
            table: "OrderFulfilmentStates");
    }
}
