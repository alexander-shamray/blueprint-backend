using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>
/// §8.5's marker gains the <c>rowversion</c> that
/// <c>RetentionPurgeService</c>'s <c>DELETE</c> identifies a row by
/// (ADR-041). ADR-038's ageing <c>SELECT</c> reads <c>CommittedAt</c>, so
/// that column stays. The empty default is <c>Array.Empty&lt;byte&gt;()</c>
/// and not the <c>new byte[0]</c> a generated migration carries, which
/// ADR-019's analyser policy refuses as CA1825 rather than warns about.
/// </summary>
public partial class AddIdempotencyMarkerRowVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            schema: "catalog",
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
            schema: "catalog",
            table: "IdempotencyMarkers");
    }
}
