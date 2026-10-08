using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>
/// The stamp ADR-075 orders a product's events after. An existing row takes its newest recorded event, its withdrawal
/// or else its publication: a price change left no column, but any made before this deploy is older than a skew.
/// </summary>
public partial class AddProductLastEventAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastEventAt",
            schema: "catalog",
            table: "Products",
            type: "datetimeoffset(7)",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE catalog.Products
            SET LastEventAt = COALESCE(WithdrawnAt, PublishedAt);
            """);

        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "LastEventAt",
            schema: "catalog",
            table: "Products",
            type: "datetimeoffset(7)",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "datetimeoffset(7)",
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LastEventAt",
            schema: "catalog",
            table: "Products");
    }
}
