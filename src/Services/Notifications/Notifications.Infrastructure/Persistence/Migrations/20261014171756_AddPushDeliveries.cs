using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>The push design's record: a notice's channel, and a delivery per push notice and installation.</summary>
/// <remarks>
/// Additive for a worker of the version before (§7.4): the channel defaults to email, which backfills every row and
/// is what that worker's inserts take, and the unique key only widens. Its rows are not: that worker claims a push
/// notice as mail, so no push app is named until it has gone (E.9.5).
/// </remarks>
public partial class AddPushDeliveries : Migration
{
    // Fields, for CA1861.
    private static readonly string[] NoticeClaimIncluded = ["LockedUntil", "Channel"];
    private static readonly string[] DeliveryClaimIncluded = ["LockedUntil", "Platform"];
    private static readonly string[] FormerNoticeClaimIncluded = ["LockedUntil"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_EventId_TemplateKey",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.AddColumn<string>(
            name: "Channel",
            schema: "notifications",
            table: "NotificationLog",
            type: "varchar(8)",
            unicode: false,
            maxLength: 8,
            nullable: false,
            defaultValue: "email");

        migrationBuilder.CreateTable(
            name: "PushDeliveries",
            schema: "notifications",
            columns: table => new
            {
                DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                InstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Platform = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ConsentEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                TemplateVersion = table.Column<int>(type: "int", nullable: true),
                Languages = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                Reason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                SendStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                Attempts = table.Column<int>(type: "int", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                TraceParent = table.Column<string>(type: "varchar(55)", unicode: false, maxLength: 55, nullable: true),
                TraceState = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PushDeliveries", x => x.DeliveryId);
                table.ForeignKey(
                    name: "FK_PushDeliveries_NotificationLog_NotificationId",
                    column: x => x.NotificationId,
                    principalSchema: "notifications",
                    principalTable: "NotificationLog",
                    principalColumn: "NotificationId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_EventId_TemplateKey_Channel",
            schema: "notifications",
            table: "NotificationLog",
            columns: ["EventId", "TemplateKey", "Channel"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog",
            column: "NextAttemptAt",
            filter: "[Status] = 'Pending'")
            .Annotation("SqlServer:Include", NoticeClaimIncluded);

        migrationBuilder.CreateIndex(
            name: "IX_PushDeliveries_ConsentEntry",
            schema: "notifications",
            table: "PushDeliveries",
            column: "ConsentEntryId",
            filter: "[ConsentEntryId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PushDeliveries_Customer",
            schema: "notifications",
            table: "PushDeliveries",
            column: "CustomerId",
            filter: "[CustomerId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_PushDeliveries_NotificationId_InstallationId",
            schema: "notifications",
            table: "PushDeliveries",
            columns: ["NotificationId", "InstallationId"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PushDeliveries_SendClaim",
            schema: "notifications",
            table: "PushDeliveries",
            column: "NextAttemptAt",
            filter: "[Status] = 'Pending'")
            .Annotation("SqlServer:Include", DeliveryClaimIncluded);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PushDeliveries",
            schema: "notifications");

        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_EventId_TemplateKey_Channel",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.DropColumn(
            name: "Channel",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_EventId_TemplateKey",
            schema: "notifications",
            table: "NotificationLog",
            columns: ["EventId", "TemplateKey"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog",
            column: "NextAttemptAt",
            filter: "[Status] = 'Pending'")
            .Annotation("SqlServer:Include", FormerNoticeClaimIncluded);
    }
}
