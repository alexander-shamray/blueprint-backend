using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// §8.5's marker gains a <c>rowversion</c>, generated from
/// <see cref="IdempotencyMarkerConfiguration"/> on
/// <c>IdempotencyMarkerCommittedAtDefault</c>'s terms, which argues the
/// column's why (ADR-041); only this file's dress is hand-authored, and the
/// <c>.Designer.cs</c> and snapshot are untouched. The one substantive edit
/// is <c>Array.Empty&lt;byte&gt;()</c> in place of the generator's own
/// <c>new byte[0]</c>, which ADR-019's analyser policy refuses as CA1825 —
/// regenerating this migration reintroduces that failure.
/// </summary>
public partial class AddIdempotencyMarkerRowVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            schema: "payments",
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
            schema: "payments",
            table: "IdempotencyMarkers");
    }
}
