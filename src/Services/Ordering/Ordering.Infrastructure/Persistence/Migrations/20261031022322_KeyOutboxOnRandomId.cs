using Common.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>
/// Draws the outbox key at random and leads the claim index with its bucket, so no index of the table fills
/// at one end (ADR-180). SQL Server alters no column's identity, so Id is dropped and added again.
/// </summary>
public partial class KeyOutboxOnRandomId : Migration
{
    private const string Schema = "ordering";

    private const string Table = "OutboxMessages";

    private const string Key = "PK_OutboxMessages";

    private const string ClaimIndex = "IX_Outbox_Unprocessed";

    private const string ClaimFilter = "[ProcessedAt] IS NULL";

    private static readonly string[] ClaimKey = [OutboxMessage.ClaimBucketColumn, "OccurredAt"];

    private static readonly string[] ClaimIncludes = ["Lane", "Attempts", "LockedUntil"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: ClaimIndex, schema: Schema, table: Table);
        migrationBuilder.DropPrimaryKey(name: Key, schema: Schema, table: Table);
        migrationBuilder.DropColumn(name: "Id", schema: Schema, table: Table);

        // Each row draws its own value. A row an older dispatcher holds then completes nothing, and is delivered
        // again once its lease ends: a duplicate, which §9.5's inbox absorbs.
        migrationBuilder.AddColumn<long>(
            name: "Id",
            schema: Schema,
            table: Table,
            type: "bigint",
            nullable: false,
            defaultValueSql: OutboxMessage.RandomIdSql);
        migrationBuilder.AddPrimaryKey(name: Key, schema: Schema, table: Table, column: "Id");

        migrationBuilder.AddColumn<byte>(
            name: OutboxMessage.ClaimBucketColumn,
            schema: Schema,
            table: Table,
            type: "tinyint",
            nullable: false,
            computedColumnSql: OutboxMessage.ClaimBucketSql,
            stored: false);
        migrationBuilder.CreateIndex(
                name: ClaimIndex, schema: Schema, table: Table, columns: ClaimKey, filter: ClaimFilter)
            .Annotation("SqlServer:Include", ClaimIncludes);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: ClaimIndex, schema: Schema, table: Table);
        migrationBuilder.DropColumn(name: OutboxMessage.ClaimBucketColumn, schema: Schema, table: Table);
        migrationBuilder.DropPrimaryKey(name: Key, schema: Schema, table: Table);
        migrationBuilder.DropColumn(name: "Id", schema: Schema, table: Table);

        migrationBuilder.AddColumn<long>(
                name: "Id",
                schema: Schema,
                table: Table,
                type: "bigint",
                nullable: false)
            .Annotation("SqlServer:Identity", "1, 1");
        migrationBuilder.AddPrimaryKey(name: Key, schema: Schema, table: Table, column: "Id");

        // ADR-161's option, which EF Core has no builder for.
        migrationBuilder.Sql(
            """
            ALTER INDEX PK_OutboxMessages ON ordering.OutboxMessages
            SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);
            """);

        migrationBuilder.CreateIndex(
                name: ClaimIndex, schema: Schema, table: Table, column: "OccurredAt", filter: ClaimFilter)
            .Annotation("SqlServer:Include", ClaimIncludes);
    }
}
