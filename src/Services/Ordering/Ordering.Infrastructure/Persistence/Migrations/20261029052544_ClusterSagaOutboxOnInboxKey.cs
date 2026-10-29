using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>MassTransit's saga outbox is clustered on its inbox-message index rather than its identity key, so a
/// delivery's DELETE and read seek on any plan (ADR-167).</summary>
public partial class ClusterSagaOutboxOnInboxKey : Migration
{
    private static readonly string[] InboxKey = ["InboxMessageId", "InboxConsumerId", "SequenceNumber"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropPrimaryKey(
            name: "PK_OutboxMessage",
            schema: "ordering",
            table: "OutboxMessage");

        migrationBuilder.DropIndex(
            name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
            schema: "ordering",
            table: "OutboxMessage");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
            schema: "ordering",
            table: "OutboxMessage",
            columns: InboxKey,
            unique: true)
            .Annotation("SqlServer:Clustered", true);

        migrationBuilder.AddPrimaryKey(
            name: "PK_OutboxMessage",
            schema: "ordering",
            table: "OutboxMessage",
            column: "SequenceNumber")
            .Annotation("SqlServer:Clustered", false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropPrimaryKey(
            name: "PK_OutboxMessage",
            schema: "ordering",
            table: "OutboxMessage");

        migrationBuilder.DropIndex(
            name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
            schema: "ordering",
            table: "OutboxMessage");

        migrationBuilder.AddPrimaryKey(
            name: "PK_OutboxMessage",
            schema: "ordering",
            table: "OutboxMessage",
            column: "SequenceNumber");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
            schema: "ordering",
            table: "OutboxMessage",
            columns: InboxKey,
            unique: true,
            filter: "[InboxMessageId] IS NOT NULL AND [InboxConsumerId] IS NOT NULL");
    }
}
