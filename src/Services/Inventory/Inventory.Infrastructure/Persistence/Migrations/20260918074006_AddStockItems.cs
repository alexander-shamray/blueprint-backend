using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Persistence.Migrations;

/// <summary>
/// §7.3's stock table, generated from <see cref="StockItemConfiguration"/> —
/// the configuration is the source of truth and only this file's dress is
/// hand-authored (file-scoped namespace, this comment). The <c>.Designer.cs</c>
/// and the snapshot beside it are machine-owned and untouched.
/// </summary>
public partial class AddStockItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "StockItems",
            schema: "inventory",
            columns: table => new
            {
                ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Available = table.Column<int>(type: "int", nullable: false),
                Reserved = table.Column<int>(type: "int", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StockItems", x => x.ProductId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "StockItems",
            schema: "inventory");
    }
}
