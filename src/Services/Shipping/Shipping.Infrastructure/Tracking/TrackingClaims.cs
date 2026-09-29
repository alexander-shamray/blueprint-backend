using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;

namespace Shipping.Infrastructure.Tracking;

/// <summary>
/// The lease and the backoff over <c>shipping.Shipments</c> for the tracking
/// pass, in <c>FulfilmentClaims</c>' shape and for its reasons.
/// </summary>
/// <remarks>
/// Raw statements rather than the repository: the claim is an atomic
/// select-and-lease the change tracker cannot express, and the failure
/// path runs with no aggregate loaded. A second class, not a parameter on
/// the first: each statement names its own population and schedule column.
/// </remarks>
internal sealed class TrackingClaims(IDbConnectionFactory connections)
{
    // Atomic claim: selects and leases in one statement, so two replicas
    // cannot take the same row. READPAST skips rows another replica holds.
    //
    // The LockedUntil predicate is what keeps this pass and the fulfilment
    // pass off each other's rows, and not the status filter: a Booked shipment
    // whose cancellation the carrier has not answered is in FulfilmentClaims'
    // second population and is pollable at the same time, so the two claims
    // overlap by design. Whichever stamps LockedUntil first holds the row until
    // its lease lapses, which is what two writers to one aggregate should be.
    private static readonly string ClaimSql =
        $"""
        WITH claimable AS (
            SELECT TOP ({TrackingWorker.ClaimBatchSize}) *
            FROM shipping.Shipments WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE Status IN ('Booked', 'Dispatched')
                AND NextPollAt IS NOT NULL
                AND NextPollAt <= SYSDATETIMEOFFSET()
                AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
            ORDER BY NextPollAt
        )
        UPDATE claimable
        SET LockedUntil = DATEADD(second, {TrackingWorker.LeaseSeconds}, SYSDATETIMEOFFSET())
        OUTPUT inserted.Id, inserted.OrderId, inserted.CarrierReference, inserted.Attempts;
        """;

    // NextPollAt where FulfilmentClaims pushes NextAttemptAt, and the same
    // ladder read from the dispatcher's own constants (spec, section 4): one
    // number tuned in two places is two backoffs that stop agreeing. Attempts
    // is the one column both workers share, which Shipment.PollApplied and
    // Shipment.ReleaseClaim both clear — a carrier that is down fails the
    // booking and the poll alike.
    //
    // Nothing is abandoned by count: the shipment's deadline is the saga's,
    // and a row that outlives it is already a review row in Ordering.
    private static readonly string FailSql =
        $"""
        UPDATE shipping.Shipments
        SET
            Attempts    = Attempts + 1,
            LockedUntil = NULL,
            NextPollAt  = DATEADD(
                second,
                POWER(2, CASE WHEN Attempts > {OutboxDispatcher.BackoffAttemptCap}
                              THEN {OutboxDispatcher.BackoffAttemptCap}
                              ELSE Attempts END) * {OutboxDispatcher.BackoffBaseSeconds},
                SYSDATETIMEOFFSET())
        WHERE Id = @Id;
        """;

    // Hands a claimed row back unchanged, for the next tick. Neither Attempts
    // nor NextPollAt moves: the pass ran out of time, which is not a fact about
    // the carrier.
    private const string ReleaseSql =
        "UPDATE shipping.Shipments SET LockedUntil = NULL WHERE Id = @Id;";

    public async Task<IReadOnlyList<TrackingWork>> ClaimAsync(CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        // CommandDefinition, so the token reaches the database command: with
        // the plain overload a shutdown cannot interrupt a blocked claim.
        return [.. await connection.QueryAsync<TrackingWork>(new CommandDefinition(ClaimSql, cancellationToken: ct))];
    }

    public async Task FailAsync(Guid id, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(FailSql, new { Id = id }, cancellationToken: ct));
    }

    public async Task ReleaseAsync(Guid id, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(ReleaseSql, new { Id = id }, cancellationToken: ct));
    }
}
