using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>
/// §9.4's outbox table, generated from <see cref="OutboxMessageConfiguration"/>, the source of truth;
/// the <c>.Designer.cs</c> and snapshot beside it are machine-owned and untouched.
/// </summary>
public partial class AddOutbox : Migration
{
    // A field, as CA1861 asks: Annotation takes an object, so a collection expression has no target type.
    private static readonly string[] IncludedColumns = ["Lane", "Attempts", "LockedUntil"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OutboxMessages",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MessageType = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Lane = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                Attempts = table.Column<int>(type: "int", nullable: false),
                LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxMessages", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Outbox_Unprocessed",
            schema: "catalog",
            table: "OutboxMessages",
            column: "OccurredAt",
            filter: "[ProcessedAt] IS NULL")
            .Annotation("SqlServer:Include", IncludedColumns);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_MessageId",
            schema: "catalog",
            table: "OutboxMessages",
            column: "MessageId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OutboxMessages",
            schema: "catalog");
    }
}
