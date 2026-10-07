using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>The send worker's restored trace (§9.4), nullable so the serving version writes rows without it (§7.4).</summary>
public partial class AddSendTraceContext : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "TraceParent",
            schema: "notifications",
            table: "NotificationLog",
            type: "varchar(55)",
            unicode: false,
            maxLength: 55,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TraceState",
            schema: "notifications",
            table: "NotificationLog",
            type: "varchar(512)",
            unicode: false,
            maxLength: 512,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TraceParent",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.DropColumn(
            name: "TraceState",
            schema: "notifications",
            table: "NotificationLog");
    }
}
