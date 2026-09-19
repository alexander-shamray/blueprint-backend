using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// §8.5's idempotency markers, generated from
/// <see cref="IdempotencyMarkerConfiguration"/> on <c>AddInbox</c>'s terms
/// — the configuration is the source of truth, and only this file's dress
/// is hand-authored; the <c>.Designer.cs</c> and snapshot are untouched.
/// This table is the one place in the schema where a missing row is a
/// correctness failure, not a lost record: it says a command committed,
/// and it is what refuses the retry of an attempt whose acknowledgement
/// was lost.
/// </summary>
public partial class AddIdempotencyMarkers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IdempotencyMarkers",
            schema: "payments",
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
            schema: "payments",
            table: "IdempotencyMarkers",
            column: "CommittedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "IdempotencyMarkers",
            schema: "payments");
    }
}
