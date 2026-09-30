using Microsoft.EntityFrameworkCore.Migrations;

namespace Shipping.Infrastructure.Persistence.Migrations;

/// <summary>§8.5's marker gains the database-clock default on <c>CommittedAt</c> that ADR-038 decides.</summary>
public partial class IdempotencyMarkerCommittedAtDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "CommittedAt",
            schema: "shipping",
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
            schema: "shipping",
            table: "IdempotencyMarkers",
            type: "datetimeoffset(7)",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "datetimeoffset(7)",
            oldDefaultValueSql: "SYSDATETIMEOFFSET()");
    }
}
