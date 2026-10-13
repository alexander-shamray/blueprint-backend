using Microsoft.EntityFrameworkCore.Migrations;

namespace Web.Bff.Persistence.Migrations;

/// <summary>A customer's saved delivery address, one row each, keyed by the customer (ADR-107).</summary>
public partial class AddSavedAddresses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SavedAddresses",
            schema: "bff",
            columns: table => new
            {
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Line1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Line2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                Country = table.Column<string>(type: "nchar(2)", fixedLength: true, maxLength: 2, nullable: false),
                SavedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SavedAddresses", x => x.CustomerId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SavedAddresses",
            schema: "bff");
    }
}
