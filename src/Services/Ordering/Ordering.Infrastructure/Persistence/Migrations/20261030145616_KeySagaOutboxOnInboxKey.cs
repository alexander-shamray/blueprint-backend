using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>MassTransit's saga outbox is keyed on its inbox-message index, with no index in staging order, and
/// its inbox draws its Id at random rather than from an identity, so no index of either puts every message on one last page
/// (ADR-177).</summary>
public partial class KeySagaOutboxOnInboxKey : Migration
{
    private const string Schema = "ordering";

    private const string Outbox = "OutboxMessage";

    private const string InboxForeignKey = "FK_OutboxMessage_InboxState_InboxMessageId_InboxConsumerId";

    private const string InboxIndex = "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber";

    private static readonly string[] InboxKey = ["InboxMessageId", "InboxConsumerId", "SequenceNumber"];

    private static readonly string[] ForeignKeyColumns = ["InboxMessageId", "InboxConsumerId"];

    private static readonly string[] MessageKey = ["MessageId", "ConsumerId"];

    private static readonly string[] StagedAt = ["EnqueueTime", "ExpirationTime"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The foreign key holds the two columns, which SQL Server will not make NOT NULL beneath it.
        migrationBuilder.DropForeignKey(name: InboxForeignKey, schema: Schema, table: Outbox);
        migrationBuilder.DropPrimaryKey(name: "PK_OutboxMessage", schema: Schema, table: Outbox);
        migrationBuilder.DropIndex(name: InboxIndex, schema: Schema, table: Outbox);
        foreach (string column in StagedAt)
            migrationBuilder.DropIndex(name: $"IX_OutboxMessage_{column}", schema: Schema, table: Outbox);

        // No default: a bus-outbox row, which Ordering never writes, fails the migration rather than being keyed.
        foreach (string column in ForeignKeyColumns)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: column,
                schema: Schema,
                table: Outbox,
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }

        migrationBuilder.AddPrimaryKey(name: "PK_OutboxMessage", schema: Schema, table: Outbox, columns: InboxKey)
            .Annotation("SqlServer:Clustered", true);
        AddInboxForeignKey(migrationBuilder);

        // SQL Server alters no column's identity, so Id is dropped and added again, each row drawing its own value.
        migrationBuilder.DropPrimaryKey(name: "PK_InboxState", schema: Schema, table: "InboxState");
        migrationBuilder.DropColumn(name: "Id", schema: Schema, table: "InboxState");
        migrationBuilder.AddColumn<long>(
            name: "Id",
            schema: Schema,
            table: "InboxState",
            type: "bigint",
            nullable: false,
            defaultValueSql: OrderingDbContext.InboxStateRandomId);
        migrationBuilder.AddPrimaryKey(name: "PK_InboxState", schema: Schema, table: "InboxState", column: "Id")
            .Annotation("SqlServer:Clustered", false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropPrimaryKey(name: "PK_InboxState", schema: Schema, table: "InboxState");
        migrationBuilder.DropColumn(name: "Id", schema: Schema, table: "InboxState");
        migrationBuilder.AddColumn<long>(
                name: "Id",
                schema: Schema,
                table: "InboxState",
                type: "bigint",
                nullable: false)
            .Annotation("SqlServer:Identity", "1, 1");
        migrationBuilder.AddPrimaryKey(name: "PK_InboxState", schema: Schema, table: "InboxState", column: "Id")
            .Annotation("SqlServer:Clustered", false);

        migrationBuilder.DropForeignKey(name: InboxForeignKey, schema: Schema, table: Outbox);
        migrationBuilder.DropPrimaryKey(name: "PK_OutboxMessage", schema: Schema, table: Outbox);
        foreach (string column in ForeignKeyColumns)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: column,
                schema: Schema,
                table: Outbox,
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");
        }

        migrationBuilder.CreateIndex(
                name: InboxIndex, schema: Schema, table: Outbox, columns: InboxKey, unique: true)
            .Annotation("SqlServer:Clustered", true);
        migrationBuilder.AddPrimaryKey(
                name: "PK_OutboxMessage", schema: Schema, table: Outbox, column: "SequenceNumber")
            .Annotation("SqlServer:Clustered", false);
        foreach (string column in StagedAt)
            migrationBuilder.CreateIndex(
                name: $"IX_OutboxMessage_{column}", schema: Schema, table: Outbox, column: column);
        AddInboxForeignKey(migrationBuilder);
    }

    private static void AddInboxForeignKey(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddForeignKey(
            name: InboxForeignKey,
            schema: Schema,
            table: Outbox,
            columns: ForeignKeyColumns,
            principalSchema: Schema,
            principalTable: "InboxState",
            principalColumns: MessageKey);
}
