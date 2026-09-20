using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>
/// §8.5's marker gains the <c>rowversion</c> that
/// <c>RetentionPurgeService</c>'s <c>DELETE</c> identifies a row by
/// (ADR-041), replacing <c>(Key, CommittedAt)</c>; ADR-038's ageing
/// <c>SELECT</c> still reads <c>CommittedAt</c>, so that column stays.
/// Generated, with one edit: the generator writes <c>new byte[0]</c>, which
/// ADR-019's analyser policy refuses as CA1825, so it reads
/// <c>Array.Empty&lt;byte&gt;()</c> here. Regenerating this file brings the
/// build failure back, and the fix is that line rather than a suppression.
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
