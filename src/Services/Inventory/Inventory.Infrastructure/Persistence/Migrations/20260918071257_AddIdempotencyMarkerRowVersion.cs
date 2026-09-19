using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Persistence.Migrations;

/// <summary>
/// §8.5's marker gains a <c>rowversion</c>, generated from
/// <see cref="IdempotencyMarkerConfiguration"/> on
/// <c>IdempotencyMarkerCommittedAtDefault</c>'s terms — the configuration is
/// the source of truth and only this file's dress is hand-authored
/// (file-scoped namespace, this comment). The <c>.Designer.cs</c> and the
/// snapshot beside it are machine-owned and untouched.
/// </summary>
/// <remarks>
/// <b>The column is what <c>RetentionPurgeService</c>'s <c>DELETE</c>
/// identifies a row by, and nothing else reads it.</b> ADR-039 split that
/// purge into select, ask and delete, and the delete names the rows the select
/// returned rather than describing them — because a key names a <em>command</em>
/// and not a row, so past §8.5's guarantee a retry can commit a fresh marker
/// under a key an earlier pass already chose, and a stale delete matching on
/// the key alone removes the replacement.
/// <para>
/// <b>What it replaces is <c>CommittedAt</c> in that join, and the difference
/// is construction against constraint.</b> The pair
/// <c>(Key, CommittedAt)</c> distinguished two writes under one key because a
/// timestamp names the instant of a write — but a <c>datetimeoffset(7)</c>
/// carries no uniqueness, so a database clock set to the exact
/// 100-nanosecond tick of a row at least <c>IdempotencyWindow</c> old matched
/// the replacement and deleted it with its Redis claim still live
/// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/173">#173</see>,
/// ADR-041).
/// A <c>rowversion</c> is SQL Server's own database-wide counter: unique,
/// monotonic, and reading no clock at all.
/// </para>
/// <para>
/// <b><c>CommittedAt</c> is not dropped and its index is not touched.</b> It
/// still ages the candidate <c>SELECT</c>, which is the half ADR-038 argued
/// and this change leaves exactly where it was — the column stops deciding
/// <em>which row is which</em> and goes on deciding <em>which rows have served
/// their window</em>.
/// </para>
/// <para>
/// <b>Adding it to a populated table is safe without a backfill.</b> SQL
/// Server stamps a <c>rowversion</c> on every existing row as part of the
/// <c>ALTER TABLE</c> and on every insert and update after it, which is why
/// the column is <c>NOT NULL</c> with no data migration behind it. The
/// <c>defaultValue</c> the generator emits is inert for this type — SQL Server
/// generates the value and the DDL carries no <c>DEFAULT</c> constraint — and
/// it is the one substantive edit in this file: the generator writes
/// <c>new byte[0]</c>, which ADR-019's analyser policy refuses as CA1825, so it
/// reads <c>Array.Empty&lt;byte&gt;()</c> here. Regenerating this migration
/// reintroduces the build failure, and the fix is this line rather than a
/// suppression.
/// </para>
/// </remarks>
public partial class AddIdempotencyMarkerRowVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            schema: "inventory",
            table: "IdempotencyMarkers",
            type: "rowversion",
            rowVersion: true,
            nullable: false,
            defaultValue: Array.Empty<byte>());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RowVersion",
            schema: "inventory",
            table: "IdempotencyMarkers");
    }
}
