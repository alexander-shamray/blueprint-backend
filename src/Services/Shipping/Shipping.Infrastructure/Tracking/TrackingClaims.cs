using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;
using Shipping.Infrastructure.Carrier;

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
    //
    // NextPollAt IS NOT NULL repeats ShipmentConfiguration's index filter,
    // which is what lets the optimiser match it.
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
        OUTPUT inserted.Id, inserted.OrderId, inserted.CarrierReference, inserted.PollAttempts, inserted.CreatedAt;
        """;

    // NextPollAt where FulfilmentClaims pushes NextAttemptAt, on the ladder
    // read from the dispatcher's own constants (spec, section 4): one number
    // tuned in two places is two backoffs that stop agreeing. Floored at
    // CarrierHop.TrackingPollInterval, because a ladder step below it would
    // answer a 429 by polling sooner than a healthy row is polled. The count
    // is this worker's own, so a feed that fails climbs its own ladder and a
    // cancel that fails climbs the other's (ADR-054).
    private static readonly string FailSql =
        $"""
        UPDATE shipment
        SET
            PollAttempts = shipment.PollAttempts + 1,
            LockedUntil  = NULL,
            NextPollAt   = DATEADD(
                second,
                CASE WHEN ladder.Seconds > {(int)CarrierHop.TrackingPollInterval.TotalSeconds}
                     THEN ladder.Seconds
                     ELSE {(int)CarrierHop.TrackingPollInterval.TotalSeconds} END,
                SYSDATETIMEOFFSET())
        FROM shipping.Shipments AS shipment
        CROSS APPLY (VALUES (
            POWER(2, CASE WHEN shipment.PollAttempts > {OutboxDispatcher.BackoffAttemptCap}
                          THEN {OutboxDispatcher.BackoffAttemptCap}
                          ELSE shipment.PollAttempts END) * {OutboxDispatcher.BackoffBaseSeconds}
        )) AS ladder (Seconds)
        WHERE shipment.Id = @Id;
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

    // No count abandons a row: a shipment leaves the poll when it is terminal
    // or past TrackingWorker.GiveUpAge, which the pass reads (ADR-054).
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
