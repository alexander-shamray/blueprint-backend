using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>
/// §9.5's inbox table, generated from <see cref="InboxMessageConfiguration"/>
/// on <c>AddOutbox</c>'s terms: the configuration is the source of truth, and
/// the <c>.Designer.cs</c> and snapshot beside it are machine-owned.
/// </summary>
/// <remarks>
/// The table ships before a service's first consumer because
/// <c>RetentionPurgeService</c> runs from first boot and deletes from every
/// table it was given, and a missing table logs a failed purge every pass.
/// </remarks>
public partial class AddInbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InboxMessages",
            schema: "catalog",
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
            schema: "catalog",
            table: "InboxMessages",
            column: "HandledAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "InboxMessages",
            schema: "catalog");
    }
}
