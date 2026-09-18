using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Persistence.Migrations;

/// <summary>
/// The two columns <see cref="ReservationConfiguration"/> adds for despatch
/// tracking (ADR-029): <c>DespatchedUnreservedAt</c>, the domain's own
/// timestamp, and <c>UnreservedCounted</c>, the projection's shadow claim
/// over it (§13.3).
/// </summary>
public partial class AddDespatchTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DespatchedUnreservedAt",
            schema: "inventory",
            table: "Reservations",
            type: "datetimeoffset(7)",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "UnreservedCounted",
            schema: "inventory",
            table: "Reservations",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DespatchedUnreservedAt",
            schema: "inventory",
            table: "Reservations");

        migrationBuilder.DropColumn(
            name: "UnreservedCounted",
            schema: "inventory",
            table: "Reservations");
    }
}
