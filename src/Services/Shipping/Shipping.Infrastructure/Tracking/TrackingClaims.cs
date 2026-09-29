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
    /// <summary>
    /// The rows a claim would take now, due and held by no pass, which
    /// <c>ShipmentStats</c> measures rather than a copy. The LockedUntil
    /// predicate, not the status filter, keeps the two passes off each other's
    /// rows: a Booked shipment awaiting its cancellation's answer is in both
    /// claims by design. NextPollAt IS NOT NULL repeats ShipmentConfiguration's
    /// index filter, so it matches.
    /// </summary>
    internal const string Claimable =
        """
        Status IN ('Booked', 'Dispatched')
            AND NextPollAt IS NOT NULL
            AND NextPollAt <= SYSDATETIMEOFFSET()
            AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
        """;

    // Atomic claim: selects and leases in one statement, so two replicas
    // cannot take the same row. READPAST skips rows another replica holds.
    private static readonly string ClaimSql =
        $"""
        WITH claimable AS (
            SELECT TOP ({TrackingWorker.ClaimBatchSize}) *
            FROM shipping.Shipments WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE {Claimable}
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
}
