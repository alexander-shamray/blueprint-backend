using Microsoft.EntityFrameworkCore.Migrations;

namespace Web.Bff.Persistence.Migrations;

/// <summary>A saved address keeps the erasure requests its customer was under when it was saved (ADR-135).</summary>
public partial class AddSavedAddressErasedUnder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ErasedUnder",
            schema: "bff",
            table: "SavedAddresses",
            type: "nvarchar(max)",
            nullable: false,
            defaultValueSql: "N'[]'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ErasedUnder",
            schema: "bff",
            table: "SavedAddresses");
    }
}
