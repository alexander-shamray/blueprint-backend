using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;

namespace Shipping.Infrastructure.Fulfilment;

/// <summary>
/// The lease and the backoff over <c>shipping.Shipments</c>, in
/// <c>OutboxDispatcher</c>'s shape and for its reasons.
/// </summary>
/// <remarks>
/// Raw statements rather than the repository: the claim is an atomic
/// select-and-lease one statement cannot express through the change tracker,
/// and the failure path runs when the aggregate was never loaded.
/// </remarks>
internal sealed class FulfilmentClaims(IDbConnectionFactory connections)
{
    // Atomic claim: selects and leases in one statement, so two replicas
    // cannot take the same row. READPAST skips rows another replica holds.
    //
    // Two populations, one claim: a Pending shipment to book, and a Booked one
    // whose cancellation the carrier has not answered yet (spec, section 5).
    // Every terminal state is outside both, so nothing already finished is
    // ever claimed.
    private static readonly string ClaimSql =
        $"""
        WITH claimable AS (
            SELECT TOP ({FulfilmentWorker.ClaimBatchSize}) *
            FROM shipping.Shipments WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE NextAttemptAt <= SYSDATETIMEOFFSET()
                AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
                AND (Status = 'Pending'
                     OR (Status = 'Booked'
                         AND CancellationRequestedAt IS NOT NULL
                         AND CancellationRefusedAt IS NULL))
            ORDER BY NextAttemptAt
        )
        UPDATE claimable
        SET LockedUntil = DATEADD(second, {FulfilmentWorker.LeaseSeconds}, SYSDATETIMEOFFSET())
        OUTPUT inserted.Id, inserted.OrderId, inserted.Status, inserted.CarrierReference, inserted.CreatedAt;
        """;

    // Increments the attempt counter and backs off by pushing NextAttemptAt
    // forward, and drops the lease so a replica does not wait out a minute for
    // a row that is already scheduled. The ladder is the dispatcher's
    // (spec, section 4), read from its constants so the two cannot drift.
    //
    // Nothing is abandoned by count: what ends the retrying is the row's age,
    // which the pass reads against FulfilmentOptions.GiveUpAge (ADR-052).
    private static readonly string FailSql =
        $"""
        UPDATE shipping.Shipments
        SET
            Attempts      = Attempts + 1,
            LockedUntil   = NULL,
            NextAttemptAt = DATEADD(
                second,
                POWER(2, CASE WHEN Attempts > {OutboxDispatcher.BackoffAttemptCap}
                              THEN {OutboxDispatcher.BackoffAttemptCap}
                              ELSE Attempts END) * {OutboxDispatcher.BackoffBaseSeconds},
                SYSDATETIMEOFFSET())
        WHERE Id = @Id;
        """;

    public async Task<IReadOnlyList<FulfilmentWork>> ClaimAsync(CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        // CommandDefinition, so the token reaches the database command: with
        // the plain overload a shutdown cannot interrupt a blocked claim.
        return [.. await connection.QueryAsync<FulfilmentWork>(new CommandDefinition(ClaimSql, cancellationToken: ct))];
    }

    public async Task FailAsync(Guid id, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(FailSql, new { Id = id }, cancellationToken: ct));
    }
}
