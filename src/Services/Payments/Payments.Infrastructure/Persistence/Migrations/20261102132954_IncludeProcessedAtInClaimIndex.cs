using Microsoft.EntityFrameworkCore.Migrations;

namespace Payments.Infrastructure.Persistence.Migrations;

/// <summary>Includes ProcessedAt in the claim index, so a read through it needs no lookup (ADR-194).</summary>
/// <remarks>
/// Rebuilt in one statement, since the outbox gauges name the index in a hint and fail while it is absent. Backward
/// compatible with the version before (§7.4): the name, key and filter are unchanged, and only the leaf gains a column.
/// </remarks>
public partial class IncludeProcessedAtInClaimIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            CREATE INDEX IX_Outbox_Unprocessed
                ON payments.OutboxMessages (ClaimBucket, OccurredAt)
                INCLUDE (Lane, Attempts, LockedUntil, ProcessedAt)
                WHERE ProcessedAt IS NULL
                WITH (DROP_EXISTING = ON);
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            CREATE INDEX IX_Outbox_Unprocessed
                ON payments.OutboxMessages (ClaimBucket, OccurredAt)
                INCLUDE (Lane, Attempts, LockedUntil)
                WHERE ProcessedAt IS NULL
                WITH (DROP_EXISTING = ON);
            """);
}
