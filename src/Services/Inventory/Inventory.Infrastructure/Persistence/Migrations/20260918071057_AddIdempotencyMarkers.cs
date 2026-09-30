using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Persistence.Migrations;

/// <summary>§8.5's idempotency markers, generated from <see cref="IdempotencyMarkerConfiguration"/>.</summary>
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
