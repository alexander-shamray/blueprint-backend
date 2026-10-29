using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>MassTransit's InboxState is clustered on its message key rather than its identity key, so a message's
/// three statements land where its id sorts rather than on one last page (ADR-171).</summary>
public partial class ClusterInboxStateOnMessageKey : Migration
{
    private static readonly string[] MessageKey = ["MessageId", "ConsumerId"];

    private static readonly string[] InboxKey = ["InboxMessageId", "InboxConsumerId"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        Recluster(migrationBuilder, messageKeyClustered: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        Recluster(migrationBuilder, messageKeyClustered: false);
    }

    // The saga outbox's foreign key references the message key, which SQL Server will not drop beneath it; EF Core
    // leaves the foreign key out of its plan because the key it references keeps its columns.
    private static void Recluster(MigrationBuilder migrationBuilder, bool messageKeyClustered)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_OutboxMessage_InboxState_InboxMessageId_InboxConsumerId",
            schema: "ordering",
            table: "OutboxMessage");

        migrationBuilder.DropUniqueConstraint(
            name: "AK_InboxState_MessageId_ConsumerId",
            schema: "ordering",
            table: "InboxState");

        migrationBuilder.DropPrimaryKey(
            name: "PK_InboxState",
            schema: "ordering",
            table: "InboxState");

        migrationBuilder.AddUniqueConstraint(
            name: "AK_InboxState_MessageId_ConsumerId",
            schema: "ordering",
            table: "InboxState",
            columns: MessageKey)
            .Annotation("SqlServer:Clustered", messageKeyClustered);

        migrationBuilder.AddPrimaryKey(
            name: "PK_InboxState",
            schema: "ordering",
            table: "InboxState",
            column: "Id")
            .Annotation("SqlServer:Clustered", !messageKeyClustered);

        migrationBuilder.AddForeignKey(
            name: "FK_OutboxMessage_InboxState_InboxMessageId_InboxConsumerId",
            schema: "ordering",
            table: "OutboxMessage",
            columns: InboxKey,
            principalSchema: "ordering",
            principalTable: "InboxState",
            principalColumns: MessageKey);
    }
}
