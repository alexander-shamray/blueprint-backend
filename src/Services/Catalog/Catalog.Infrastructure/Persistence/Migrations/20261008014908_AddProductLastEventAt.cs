using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>
/// The stamp ADR-075 orders a product's events after. An existing row takes its newest recorded event, its withdrawal
/// or else its publication; the default is for the release still running beside this one, whose insert omits it.
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
            nullable: false,
            defaultValueSql: "SYSDATETIMEOFFSET()");

        migrationBuilder.Sql(
            """
            UPDATE catalog.Products
            SET LastEventAt = COALESCE(WithdrawnAt, PublishedAt);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LastEventAt",
            schema: "catalog",
            table: "Products");
    }
}
