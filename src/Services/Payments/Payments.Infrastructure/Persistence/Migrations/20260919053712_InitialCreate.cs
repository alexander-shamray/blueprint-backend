using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>
/// This service's first migration. EF generates an empty <c>Up</c> for a model
/// with no entity types, so the schema below is hand-written,
/// which §7.4 permits and describes: DDL that EF cannot generate "rides along,
/// in the same transaction, applied by the same job, versioned by the same
/// migration history".
/// </summary>
/// <remarks>
/// The schema is the one piece of Payments's shape that exists before its first
/// table, and creating it here means the first <c>CREATE TABLE</c> lands in a
/// schema that is already there rather than being ordered against it.
/// <para>
/// This file is hand-authored and reads like the rest of the repository —
/// file-scoped namespace and all, which IDE0161 makes a build error under
/// ADR-019. The <c>.Designer.cs</c> beside it and the model snapshot are
/// machine-owned, carry an <c>auto-generated</c> header that exempts them from
/// the analysers, and are left exactly as the tool wrote them: the snapshot is
/// the input to the next <c>migrations add</c>, and an edited one produces a
/// wrong migration the moment one is run.
/// </para>
/// </remarks>
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
