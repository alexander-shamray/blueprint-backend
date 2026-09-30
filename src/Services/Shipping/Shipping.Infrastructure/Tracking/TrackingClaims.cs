using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Tracking;

/// <summary>The tracking pass's lease and backoff, in <see cref="Fulfilment.FulfilmentClaims"/>' shape.</summary>
internal sealed class TrackingClaims(IDbConnectionFactory connections)
{
    /// <summary>The lease, not the status, keeps the passes apart: a Booked row to cancel is in both claims.</summary>
    internal const string Claimable =
        """
        Status IN ('Booked', 'Dispatched')
            AND NextPollAt IS NOT NULL
            AND NextPollAt <= SYSDATETIMEOFFSET()
            AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
        """;

    // One statement selects and leases, so two replicas cannot take one row; READPAST skips another's.
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

    // The dispatcher's ladder on this worker's own count (ADR-054), floored at the poll interval so a 429 is
    // never answered by polling sooner than a healthy row is.
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

        // CommandDefinition, so a shutdown's token can interrupt a blocked claim.
        return [.. await connection.QueryAsync<TrackingWork>(new CommandDefinition(ClaimSql, cancellationToken: ct))];
    }

    // No count abandons a row: it leaves the poll when terminal or past TrackingWorker.GiveUpAge (ADR-054).
    public async Task FailAsync(Guid id, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(FailSql, new { Id = id }, cancellationToken: ct));
    }
}
