using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// §9.4's outbox table, generated from <see cref="OutboxMessageConfiguration"/>
/// — the configuration is the source of truth and only this file's dress is
/// hand-authored (file-scoped namespace, this comment, the field CA1861 asks
/// for). The <c>.Designer.cs</c> and the snapshot beside it are machine-owned
/// and untouched.
/// </summary>
/// <remarks>
/// <c>IX_Outbox_Unprocessed</c> is filtered and covering, and both halves earn
/// their place: the dispatcher claims twice a second and only ever reads rows
/// with a null <c>ProcessedAt</c>, so the index stays the size of the backlog
/// rather than the size of the table, and the included columns are exactly
/// what the claim's predicate needs beyond the key.
/// </remarks>
public partial class AddOutbox : Migration
{
    // A field rather than the generated `new[] { … }` argument, which is what
    // CA1861 asks for and the only shape available: Annotation takes an
    // object, so a collection expression has no target type to convert to.
    // A CreateIndex call meets the same rule inline, because its columns
    // parameter is a string[] and a collection expression converts to one.
    private static readonly string[] IncludedColumns = ["Lane", "Attempts", "LockedUntil"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OutboxMessages",
            schema: "payments",
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
            schema: "payments",
            table: "OutboxMessages",
            column: "OccurredAt",
            filter: "[ProcessedAt] IS NULL")
            .Annotation("SqlServer:Include", IncludedColumns);

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_MessageId",
            schema: "payments",
            table: "OutboxMessages",
            column: "MessageId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OutboxMessages",
            schema: "payments");
    }
}
