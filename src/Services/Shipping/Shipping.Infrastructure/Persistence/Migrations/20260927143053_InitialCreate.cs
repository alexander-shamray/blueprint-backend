using Microsoft.EntityFrameworkCore.Migrations;

namespace Shipping.Infrastructure.Persistence.Migrations;

/// <summary>The schema, hand-written as §7.4 permits, since EF generates nothing for an empty model.</summary>
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.EnsureSchema("shipping");

    // Unreachable, since §7.4 rolls forward, but a migration is the record of a change and this undoes its Up.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP SCHEMA [shipping];");
}
