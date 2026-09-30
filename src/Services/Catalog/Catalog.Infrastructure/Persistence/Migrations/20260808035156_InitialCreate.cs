using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>
/// This service's first migration, hand-written as §7.4 permits because EF generates an empty <c>Up</c>
/// for a model with no entities; the <c>.Designer.cs</c> and snapshot stay as the tool wrote them.
/// </summary>
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.EnsureSchema("catalog");

    // Unreachable, since §7.4 rolls forward, and written so this record of a change states its inverse.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP SCHEMA [catalog];");
}
