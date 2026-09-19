using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// §9.5's inbox table, generated from <see cref="InboxMessageConfiguration"/>
/// — the configuration is the source of truth for the column and collation
/// choices below, and only this file's dress is hand-authored (file-scoped
/// namespace, this comment); the <c>.Designer.cs</c> and snapshot beside it
/// are machine-owned and untouched. The table arrives before the first
/// consumer, deliberately: <c>RetentionPurgeService</c> runs from first boot
/// and deletes from every table it was given, and a purge against a missing
/// table logs a failure every pass.
/// </summary>
public partial class AddInbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InboxMessages",
            schema: "payments",
            columns: table => new
            {
                MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Endpoint = table.Column<string>(
                    type: "nvarchar(300)",
                    maxLength: 300,
                    nullable: false,
                    collation: "Latin1_General_BIN2"),
                HandledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InboxMessages", x => new { x.MessageId, x.Endpoint });
            });

        migrationBuilder.CreateIndex(
            name: "IX_Inbox_HandledAt",
            schema: "payments",
            table: "InboxMessages",
            column: "HandledAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "InboxMessages",
            schema: "payments");
    }
}
