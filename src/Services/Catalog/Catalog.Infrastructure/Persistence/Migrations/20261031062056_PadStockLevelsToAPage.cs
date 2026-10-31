using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>Pads each stock level to a page of its own, so no two products share a page's latch (ADR-183).</summary>
public partial class PadStockLevelsToAPage : Migration
{
    private const string Schema = "catalog";

    private const string Table = "StockLevels";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Padding",
            schema: Schema,
            table: Table,
            type: "char(4100)",
            maxLength: 4100,
            nullable: false,
            defaultValue: "");

        // Adding a column with a constant default writes no row, so the rows already there would share their page
        // until each was next updated; the rebuild writes them out a page each now.
        migrationBuilder.Sql("ALTER INDEX PK_StockLevels ON catalog.StockLevels REBUILD;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Padding", schema: Schema, table: Table);
    }
}
