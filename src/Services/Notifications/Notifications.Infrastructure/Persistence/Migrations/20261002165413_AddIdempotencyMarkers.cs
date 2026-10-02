using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>
/// §8.5's idempotency markers, generated from <see cref="IdempotencyMarkerConfiguration"/> on
/// <c>AddInbox</c>'s terms: the one table where a missing row is a correctness failure, not a lost record.
/// </summary>
public partial class AddIdempotencyMarkers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IdempotencyMarkers",
            schema: "notifications",
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
            schema: "notifications",
            table: "IdempotencyMarkers",
            column: "CommittedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "IdempotencyMarkers",
            schema: "notifications");
    }
}
