using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>A device row and a journal entry keep the requests their subject was under (ADR-138).</summary>
public partial class AddErasedUnderToDevicesAndConsent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ErasedUnder",
            schema: "notifications",
            table: "DeviceInstallations",
            type: "nvarchar(max)",
            nullable: false,
            defaultValueSql: "N'[]'");

        migrationBuilder.AddColumn<string>(
            name: "ErasedUnder",
            schema: "notifications",
            table: "ConsentEntries",
            type: "nvarchar(max)",
            nullable: false,
            defaultValueSql: "N'[]'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ErasedUnder",
            schema: "notifications",
            table: "DeviceInstallations");

        migrationBuilder.DropColumn(
            name: "ErasedUnder",
            schema: "notifications",
            table: "ConsentEntries");
    }
}
