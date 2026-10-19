using Microsoft.EntityFrameworkCore.Migrations;

namespace Web.Bff.Persistence.Migrations;

/// <summary>An order keeps the erasure requests its customer was already under when it was placed (ADR-099).</summary>
public partial class AddOrderErasedUnder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ErasedUnder",
            schema: "bff",
            table: "Orders",
            type: "nvarchar(max)",
            nullable: false,
            defaultValueSql: "N'[]'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ErasedUnder",
            schema: "bff",
            table: "Orders");
    }
}
