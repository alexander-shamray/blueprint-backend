using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Web.Bff.Persistence.Migrations;

/// <summary>The filtered index behind the unattributed gauge's seek, generated from its configuration.</summary>
public partial class IndexUnattributedOrders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_Orders_Unattributed",
            schema: "bff",
            table: "Orders",
            column: "FirstSeenAt",
            filter: "[CustomerId] IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Orders_Unattributed",
            schema: "bff",
            table: "Orders");
    }
}
