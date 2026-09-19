using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>
/// §3.2's one Catalog projection table, <c>catalog.StockLevels</c>, generated
/// from <see cref="StockLevelConfiguration"/> — the configuration is the source
/// of truth and only this file's dress is hand-authored (file-scoped namespace,
/// this comment). The <c>.Designer.cs</c> and the snapshot beside it are
/// machine-owned and untouched.
/// </summary>
public partial class AddStockLevels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "StockLevels",
            schema: "catalog",
            columns: table => new
            {
                ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                QuantityAvailable = table.Column<int>(type: "int", nullable: false),
                AsOf = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StockLevels", x => x.ProductId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "StockLevels",
            schema: "catalog");
    }
}
