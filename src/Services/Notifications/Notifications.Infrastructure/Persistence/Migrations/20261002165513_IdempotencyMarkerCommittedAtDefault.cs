using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>
/// §8.5's marker gains a <c>SYSDATETIMEOFFSET()</c> default on <c>CommittedAt</c> (ADR-038), generated from
/// <see cref="IdempotencyMarkerConfiguration"/>; the inbox, whose window is housekeeping, is left alone.
/// </summary>
public partial class IdempotencyMarkerCommittedAtDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "CommittedAt",
            schema: "notifications",
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
            schema: "notifications",
            table: "IdempotencyMarkers",
            type: "datetimeoffset(7)",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "datetimeoffset(7)",
            oldDefaultValueSql: "SYSDATETIMEOFFSET()");
    }
}
