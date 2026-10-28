using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Delivery;

/// <summary>ADR-052's lease and backoff over <c>PushDeliveries</c>, in <see cref="SendClaims"/>' shape.</summary>
internal sealed class PushClaims(IDbConnectionFactory connections, IOptions<SendPacingOptions> pacing)
{
    /// <summary>A pending row due and unleased; the first line repeats the claim index's filter.</summary>
    internal const string Claimable =
        """
        Status = 'Pending'
            AND NextAttemptAt <= SYSDATETIMEOFFSET()
            AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
        """;

    // One statement selects and leases, so two replicas cannot take one row; READPAST skips another's. A platform whose
    // provider's breaker is open is left out, so its rows keep their backoff and the other platform's are sent. The
    // batch and the lease are parameters, as SendPacingOptions configures them.
    private static readonly string ClaimSql =
        $"""
        WITH claimable AS (
            SELECT TOP (@BatchSize) *
            FROM notifications.PushDeliveries WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE {Claimable}
                AND Platform NOT IN @Parked
            ORDER BY NextAttemptAt
        )
        UPDATE claimable
        SET LockedUntil = DATEADD(second, @LeaseSeconds, SYSDATETIMEOFFSET())
        OUTPUT inserted.DeliveryId, inserted.NotificationId, inserted.InstallationId, inserted.Platform,
            inserted.CustomerId, inserted.ConsentEntryId, inserted.TemplateVersion, inserted.Languages,
            inserted.SendStartedAt, inserted.CorrelationId, inserted.TraceParent, inserted.TraceState,
            inserted.LockedUntil;
        """;

    // The notice's ladder, from the dispatcher's constants so the three cannot drift. No count abandons a row: waiting
    // ends at the notice's give-up age. Only under the caller's lease, for SendClaims' reason.
    private static readonly string BackOffSql =
        $"""
        UPDATE notifications.PushDeliveries
        SET
            Attempts      = Attempts + 1,
            LockedUntil   = NULL,
            NextAttemptAt = DATEADD(
                second,
                POWER(2, CASE WHEN Attempts > {OutboxDispatcher.BackoffAttemptCap}
                              THEN {OutboxDispatcher.BackoffAttemptCap}
                              ELSE Attempts END) * {OutboxDispatcher.BackoffBaseSeconds},
                SYSDATETIMEOFFSET())
        WHERE DeliveryId = @DeliveryId
            AND Status = 'Pending'
            AND LockedUntil = @Lease;
        """;

    /// <summary>Leases due deliveries of every platform but <paramref name="parked"/>'s.</summary>
    public async Task<IReadOnlyList<PushWork>> ClaimAsync(IReadOnlyList<string> parked, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();
        SendPacingOptions pace = pacing.Value;

        // An empty list expands to a predicate matching nothing, so NOT IN leaves every platform in.
        return
        [
            .. await connection.QueryAsync<PushWork>(
                new CommandDefinition(
                    ClaimSql,
                    new { Parked = parked, BatchSize = pace.PushClaimBatchSize, LeaseSeconds = pace.PushLeaseSeconds },
                    cancellationToken: ct))
        ];
    }

    /// <summary>Backs the row off, if it is still under <paramref name="lease"/>; true when it was.</summary>
    public async Task<bool> BackOffAsync(Guid deliveryId, DateTimeOffset lease, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return await connection.ExecuteAsync(
            new CommandDefinition(
                BackOffSql,
                new { DeliveryId = deliveryId, Lease = lease },
                cancellationToken: ct)) == 1;
    }
}
