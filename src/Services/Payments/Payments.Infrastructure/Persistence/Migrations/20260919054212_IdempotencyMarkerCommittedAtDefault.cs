using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// §8.5's marker gains a <c>SYSDATETIMEOFFSET()</c> default on
/// <c>CommittedAt</c>, generated from
/// <see cref="IdempotencyMarkerConfiguration"/> on
/// <c>AddIdempotencyMarkers</c>' terms, which argues the default's why
/// (ADR-038); only this file's dress is hand-authored, and the
/// <c>.Designer.cs</c> and snapshot are untouched. The outbox and the inbox
/// are deliberately not altered the same way: their windows are
/// housekeeping, where this one purges the row that refuses a duplicate.
/// </summary>
public partial class IdempotencyMarkerCommittedAtDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "CommittedAt",
            schema: "payments",
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
            schema: "payments",
            table: "IdempotencyMarkers",
            type: "datetimeoffset(7)",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "datetimeoffset(7)",
            oldDefaultValueSql: "SYSDATETIMEOFFSET()");
    }
}
