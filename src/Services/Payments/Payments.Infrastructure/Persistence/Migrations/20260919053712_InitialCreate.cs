using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// This service's first migration. EF generates an empty <c>Up</c> for a
/// model with no entity types, so the schema below is hand-written, which
/// §7.4 permits: DDL EF cannot generate rides along in the same transaction,
/// applied by the same job, versioned by the same history. Hand-authored
/// like the rest of the repository, unlike the machine-owned
/// <c>.Designer.cs</c> and model snapshot beside it, which are left exactly
/// as the tool wrote them — an edited snapshot feeds a wrong migration.
/// </summary>
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.EnsureSchema("payments");

    // Unreachable in any deployment — §7.4 rolls forward, and a rename is a
    // multi-release operation rather than a Down. Written anyway, because a
    // Down that does not undo its Up is a lie in the one file whose whole job
    // is to be the record of a change.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP SCHEMA [payments];");
}
