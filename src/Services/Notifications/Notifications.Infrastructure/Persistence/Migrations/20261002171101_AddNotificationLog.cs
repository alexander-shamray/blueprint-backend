using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>§3.2's record of every notice owed, generated from <see cref="NotificationConfiguration"/>.</summary>
public partial class AddNotificationLog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "NotificationLog",
            schema: "notifications",
            columns: table => new
            {
                NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TemplateKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                TemplateVersion = table.Column<int>(type: "int", nullable: true),
                Languages = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                Parameters = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                Reason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                SendStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                Attempts = table.Column<int>(type: "int", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationLog", x => x.NotificationId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_EventId_TemplateKey",
            schema: "notifications",
            table: "NotificationLog",
            columns: ["EventId", "TemplateKey"],
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "NotificationLog",
            schema: "notifications");
    }
}
