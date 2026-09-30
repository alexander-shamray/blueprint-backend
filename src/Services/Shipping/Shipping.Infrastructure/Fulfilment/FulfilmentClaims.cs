using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;

namespace Shipping.Infrastructure.Fulfilment;

/// <summary>ADR-052's lease and backoff over the shipments, in <see cref="OutboxDispatcher"/>'s shape.</summary>
internal sealed class FulfilmentClaims(IDbConnectionFactory connections)
{
    /// <summary>A Pending row to book or a Booked one to cancel; the first two lines repeat the index filter.</summary>
    internal const string Claimable =
        """
        Status IN ('Pending', 'Booked')
            AND CancellationRefusedAt IS NULL
            AND (Status = 'Pending' OR CancellationRequestedAt IS NOT NULL)
            AND NextAttemptAt <= SYSDATETIMEOFFSET()
            AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
        """;

    // One statement selects and leases, so two replicas cannot take one row; READPAST skips another's.
    private static readonly string ClaimSql =
        $"""
        WITH claimable AS (
            SELECT TOP ({FulfilmentWorker.ClaimBatchSize}) *
            FROM shipping.Shipments WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE {Claimable}
            ORDER BY NextAttemptAt
        )
        UPDATE claimable
        SET LockedUntil = DATEADD(second, {FulfilmentWorker.LeaseSeconds}, SYSDATETIMEOFFSET())
        OUTPUT inserted.Id, inserted.OrderId, inserted.Status, inserted.CarrierReference, inserted.CreatedAt,
            inserted.CancellationRequestedAt;
        """;

    // The dispatcher's ladder, read from its constants so the two cannot drift; the lease drops with it. No
    // count abandons a row: retrying ends at FulfilmentOptions.GiveUpAge (ADR-052, ADR-054).
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

        // CommandDefinition, so a shutdown's token can interrupt a blocked claim.
        return [.. await connection.QueryAsync<FulfilmentWork>(new CommandDefinition(ClaimSql, cancellationToken: ct))];
    }

    public async Task FailAsync(Guid id, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(FailSql, new { Id = id }, cancellationToken: ct));
    }
}
