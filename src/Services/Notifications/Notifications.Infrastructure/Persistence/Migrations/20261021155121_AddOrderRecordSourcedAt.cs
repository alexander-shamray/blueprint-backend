using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>An order record keeps the earliest instant Ordering's events say its customer was named, which the
/// send pass bounds its erasure check by (ADR-139).</summary>
public partial class AddOrderRecordSourcedAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "SourcedAt",
            schema: "notifications",
            table: "OrderRecords",
            type: "datetimeoffset(7)",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SourcedAt",
            schema: "notifications",
            table: "OrderRecords");
    }
}
