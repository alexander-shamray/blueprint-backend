using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>The keyset seek of the listing's name order (ADR-073), beside the newest order's.</summary>
public partial class AddProductNameIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_Products_Name_Id",
            schema: "catalog",
            table: "Products",
            columns: ["Name", "Id"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Products_Name_Id",
            schema: "catalog",
            table: "Products");
    }
}
