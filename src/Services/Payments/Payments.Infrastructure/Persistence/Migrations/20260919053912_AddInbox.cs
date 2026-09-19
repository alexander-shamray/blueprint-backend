using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// §9.5's inbox table, generated from <see cref="InboxMessageConfiguration"/>
/// — the configuration is the source of truth and only this file's dress is
/// hand-authored (file-scoped namespace, this comment). The
/// <c>.Designer.cs</c> and the snapshot beside it are machine-owned and
/// untouched.
/// </summary>
/// <remarks>
/// <b>The table arrives before the first consumer, deliberately.</b> A service
/// that binds no receive endpoint writes no row here — but
/// <c>RetentionPurgeService</c> runs from first boot and deletes from every
/// table it was given, and a purge against a table that is not there logs a
/// failure every pass. That is the same argument that keeps the outbox
/// migration here, inverted: the dispatcher would fail a claim, this would fail
/// a delete.
/// <para>
/// <b><c>Endpoint</c> carries a binary collation because it is half a key.</b>
/// SQL Server's default is case-insensitive and a broker's queue names are
/// not, so <c>orders</c> and <c>Orders</c> — two endpoints — would collide, and
/// a message that arrived on one would be dropped as a duplicate on the other.
/// That is the once-per-endpoint guarantee the composite key exists to give,
/// defeated by how the column compares rather than by what it holds.
/// <c>BIN2</c> rather than <c>CS_AS</c>: an endpoint address is an identifier
/// matched exactly, and linguistic comparison has no meaning over it.
/// </para>
/// <para>
/// <b>And <c>nvarchar</c>, because the collation only governs what was already
/// stored.</b> This column was <c>varchar</c> for one revision on the claim that
/// queue names are ASCII; AMQP 0-9-1 allows 255 bytes of UTF-8, so two legal
/// endpoints differing outside the code page both land as the same run of
/// <c>?</c> and collide in the key — the second endpoint's message suppressed as
/// a duplicate it never was.
/// </para>
/// </remarks>
public partial class AddInbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InboxMessages",
            schema: "payments",
            columns: table => new
            {
                MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Endpoint = table.Column<string>(
                    type: "nvarchar(300)",
                    maxLength: 300,
                    nullable: false,
                    collation: "Latin1_General_BIN2"),
                HandledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InboxMessages", x => new { x.MessageId, x.Endpoint });
            });

        migrationBuilder.CreateIndex(
            name: "IX_Inbox_HandledAt",
            schema: "payments",
            table: "InboxMessages",
            column: "HandledAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "InboxMessages",
            schema: "payments");
    }
}
