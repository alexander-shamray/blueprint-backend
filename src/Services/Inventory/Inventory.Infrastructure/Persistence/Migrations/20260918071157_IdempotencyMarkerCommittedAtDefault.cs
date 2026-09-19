using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Persistence.Migrations;

/// <summary>
/// §8.5's marker gains a <c>SYSDATETIMEOFFSET()</c> default on
/// <c>CommittedAt</c>, generated from
/// <see cref="IdempotencyMarkerConfiguration"/> on
/// <c>AddIdempotencyMarkers</c>' terms — the configuration is the source of
/// truth and only this file's dress is hand-authored (file-scoped namespace,
/// this comment). The <c>.Designer.cs</c> and the snapshot beside it are
/// machine-owned and untouched.
/// </summary>
/// <remarks>
/// <b>A column default is DDL, but what it fixes is a correctness property
/// rather than a convenience.</b> <c>CommittedAt</c> was stamped from the
/// writing pod's <c>TimeProvider</c> and the purge cutoff computed on whichever
/// pod happened to run the purge; §15.3 ships three replicas of each service,
/// so the marker's <em>age</em> carried the skew between two clocks as an error
/// term. A purger leading the writer by δ deletes the marker δ early, the Redis
/// claim then expires into a table that has already forgotten the commit, and
/// the next retry runs the command a second time — the duplicate write §8.5
/// exists to refuse, arriving at a boundary nobody watches.
/// <para>
/// <b>The default is only half of it, and the other half is not in this
/// file.</b> <c>RetentionPurgeService</c> computes the marker's cutoff with
/// <c>DATEADD(second, -@WindowSeconds, SYSDATETIMEOFFSET())</c> rather than
/// passing one it worked out in the application, so both ends of the comparison
/// are the same server's clock. Either half alone leaves the skew term
/// standing ([ADR-038]).
/// </para>
/// <para>
/// <b>Writing the column explicitly still works, which is what keeps the
/// fixtures able to stage a marker at a controlled age.</b> The mapping is
/// <c>ValueGeneratedOnAdd</c> over the default, so EF omits the column while
/// the property holds its sentinel and writes it when it does not — a default
/// is what happens in the absence of a value, not a trigger that overrides one.
/// </para>
/// <para>
/// <b>The outbox and the inbox are deliberately not changed.</b> Their windows
/// are housekeeping — a purged row loses a debugging record or a suppression
/// the broker will not exercise again — where this one purges the row that
/// refuses a duplicate, and §9.5 keeps them on the registered clock precisely
/// so a test host can substitute it.
/// </para>
/// </remarks>
public partial class IdempotencyMarkerCommittedAtDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "CommittedAt",
            schema: "inventory",
            table: "IdempotencyMarkers",
            type: "datetimeoffset(7)",
            nullable: false,
            defaultValueSql: "SYSDATETIMEOFFSET()",
            oldClrType: typeof(DateTimeOffset),
            oldType: "datetimeoffset(7)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "CommittedAt",
            schema: "inventory",
            table: "IdempotencyMarkers",
            type: "datetimeoffset(7)",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "datetimeoffset(7)",
            oldDefaultValueSql: "SYSDATETIMEOFFSET()");
    }
}
