using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>
/// The seller and the withdrawal ADR-074 adds, and the seek of a seller's own list. No backfill: nothing records
/// who published an existing row, so it keeps a null seller and belongs to no caller.
/// </summary>
public partial class AddProductSellerAndWithdrawal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "SellerId",
            schema: "catalog",
            table: "Products",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "WithdrawnAt",
            schema: "catalog",
            table: "Products",
            type: "datetimeoffset(7)",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Products_SellerId_PublishedAt_Id",
            schema: "catalog",
            table: "Products",
            columns: ["SellerId", "PublishedAt", "Id"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Products_SellerId_PublishedAt_Id",
            schema: "catalog",
            table: "Products");

        migrationBuilder.DropColumn(
            name: "SellerId",
            schema: "catalog",
            table: "Products");

        migrationBuilder.DropColumn(
            name: "WithdrawnAt",
            schema: "catalog",
            table: "Products");
    }
}
