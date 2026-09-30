using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>§8.5's marker gains the <c>rowversion</c> ADR-041 decides.</summary>
/// <remarks>
/// <c>Array.Empty&lt;byte&gt;()</c> replaces the generator's <c>new byte[0]</c>, which CA1825 refuses under ADR-019,
/// so regenerating this migration reintroduces the failure.
/// </remarks>
public partial class AddIdempotencyMarkerRowVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            schema: "ordering",
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
            schema: "ordering",
            table: "IdempotencyMarkers");
    }
}
