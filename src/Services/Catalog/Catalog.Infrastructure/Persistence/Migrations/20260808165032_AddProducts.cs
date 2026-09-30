using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>The first table, §7.4's write-model kind, generated from <see cref="ProductConfiguration"/>.</summary>
/// <remarks>The generated <c>EnsureSchema</c> is redundant after <see cref="InitialCreate"/>, and idempotent.</remarks>
public partial class AddProducts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "catalog");

        migrationBuilder.CreateTable(
            name: "Products",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                ThumbnailUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                PriceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                PriceCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Products", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Products_PublishedAt_Id",
            schema: "catalog",
            table: "Products",
            columns: ["PublishedAt", "Id"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Products",
            schema: "catalog");
    }
}
