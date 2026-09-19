using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Persistence.Migrations;

/// <summary>
/// §8.5's idempotency markers, generated from
/// <see cref="IdempotencyMarkerConfiguration"/> on <c>AddInbox</c>'s terms —
/// the configuration is the source of truth and only this file's dress is
/// hand-authored (file-scoped namespace, this comment). The
/// <c>.Designer.cs</c> and the snapshot beside it are machine-owned and
/// untouched.
/// </summary>
/// <remarks>
/// <b>This table is the one place in the schema where a missing row is a
/// correctness failure rather than a lost record.</b> The outbox and the inbox
/// hold delivery state; a row here says a command committed, and it is what
/// refuses the retry of an attempt whose commit landed and whose acknowledgement
/// was lost.
/// <para>
/// <b>It ships to every service, including one that protects no command yet.</b>
/// That is <c>AddInbox</c>'s argument unchanged: <c>RetentionPurgeService</c>
/// runs from first boot and purges every table it was given, and a delete
/// against a table that is not there logs a failure every pass.
/// </para>
/// <para>
/// <b><c>Key</c> is 450 characters because that is where SQL Server's clustered
/// index key stops.</b> Two bytes a character against a 900-byte limit, so this
/// is the widest the column can be while the primary key stays clustered — and
/// the key already spends 74 of those on two GUIDs and two separators, which
/// leaves the declared operation name the rest. The binary collation is the
/// inbox's argument one table over: this column is a key, the default collation
/// is case-insensitive, and two commands are the same command only if their
/// keys are the same bytes.
/// </para>
/// </remarks>
public partial class AddIdempotencyMarkers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IdempotencyMarkers",
            schema: "inventory",
            columns: table => new
            {
                Key = table.Column<string>(
                    type: "nvarchar(450)",
                    maxLength: 450,
                    nullable: false,
                    collation: "Latin1_General_BIN2"),
                CommittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IdempotencyMarkers", x => x.Key);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Idempotency_CommittedAt",
            schema: "inventory",
            table: "IdempotencyMarkers",
            column: "CommittedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "IdempotencyMarkers",
            schema: "inventory");
    }
}
