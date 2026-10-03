using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>ADR-017's order record, generated from <see cref="OrderRecordConfiguration"/>.</summary>
public partial class AddOrderRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OrderRecords",
            schema: "notifications",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                CancelReason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                CancelOrigin = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderRecords", x => x.OrderId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OrderRecords",
            schema: "notifications");
    }
}
