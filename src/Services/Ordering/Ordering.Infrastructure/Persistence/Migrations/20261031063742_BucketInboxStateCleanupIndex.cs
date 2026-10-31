using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>MassTransit's inbox trades the library's index on Delivered for one led by a bucket of its random Id and
/// holding delivered rows only, which this service's cleanup seeks (ADR-182); generated from the model.</summary>
public partial class BucketInboxStateCleanupIndex : Migration
{
    private const string Schema = "ordering";

    private const string Inbox = "InboxState";

    private const string Bucket = "CleanupBucket";

    private const string LibraryIndex = "IX_InboxState_Delivered";

    private const string CleanupIndex = "IX_InboxState_CleanupBucket_Delivered";

    private static readonly string[] CleanupKey = [Bucket, "Delivered"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: LibraryIndex, schema: Schema, table: Inbox);

        migrationBuilder.AddColumn<byte>(
            name: Bucket,
            schema: Schema,
            table: Inbox,
            type: "tinyint",
            nullable: false,
            computedColumnSql: "CONVERT(tinyint, [Id] & 15)",
            stored: false);

        migrationBuilder.CreateIndex(
            name: CleanupIndex,
            schema: Schema,
            table: Inbox,
            columns: CleanupKey,
            filter: "[Delivered] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: CleanupIndex, schema: Schema, table: Inbox);

        migrationBuilder.DropColumn(name: Bucket, schema: Schema, table: Inbox);

        migrationBuilder.CreateIndex(name: LibraryIndex, schema: Schema, table: Inbox, column: "Delivered");
    }
}
