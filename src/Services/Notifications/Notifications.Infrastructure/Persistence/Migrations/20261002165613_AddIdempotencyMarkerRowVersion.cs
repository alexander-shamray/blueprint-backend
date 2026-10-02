using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>§8.5's marker gains the <c>rowversion</c> its purge's <c>DELETE</c> keys on (ADR-041).</summary>
/// <remarks>
/// <c>Array.Empty&lt;byte&gt;()</c>, not the generated <c>new byte[0]</c>, which ADR-019 refuses as CA1825.
/// </remarks>
public partial class AddIdempotencyMarkerRowVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            schema: "notifications",
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
            schema: "notifications",
            table: "IdempotencyMarkers");
    }
}
