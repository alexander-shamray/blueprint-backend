using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>The event's <c>CorrelationId</c> on each notice, which every message sent for it carries (§10.4).</summary>
public partial class AddNotificationCorrelationId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CorrelationId",
            schema: "notifications",
            table: "NotificationLog",
            type: "uniqueidentifier",
            nullable: false,
            defaultValue: Guid.Empty);

        // A row written before the column kept no event's value; every consumed event's publisher sets it to the
        // order (§9.3), so the row's order is the value its event carried.
        migrationBuilder.Sql("UPDATE notifications.NotificationLog SET CorrelationId = OrderId;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CorrelationId",
            schema: "notifications",
            table: "NotificationLog");
    }
}
