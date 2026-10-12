using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>An order record keeps the erasure requests its customer was already under when it was placed (ADR-101).</summary>
public partial class AddOrderRecordErasedUnder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ErasedUnder",
            schema: "notifications",
            table: "OrderRecords",
            type: "nvarchar(max)",
            nullable: false,
            defaultValueSql: "N'[]'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ErasedUnder",
            schema: "notifications",
            table: "OrderRecords");
    }
}
