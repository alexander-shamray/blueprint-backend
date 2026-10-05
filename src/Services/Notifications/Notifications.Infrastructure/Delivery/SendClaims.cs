using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;

namespace Notifications.Infrastructure.Delivery;

/// <summary>ADR-052's lease and backoff over <c>NotificationLog</c>, in <c>FulfilmentClaims</c>' shape.</summary>
internal sealed class SendClaims(IDbConnectionFactory connections)
{
    /// <summary>A pending row due and unleased; the first line repeats the claim index's filter.</summary>
    internal const string Claimable =
        """
        Status = 'Pending'
            AND NextAttemptAt <= SYSDATETIMEOFFSET()
            AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
        """;

    // One statement selects and leases, so two replicas cannot take one row; READPAST skips another's.
    private static readonly string ClaimSql =
        $"""
        WITH claimable AS (
            SELECT TOP ({SendWorker.ClaimBatchSize}) *
            FROM notifications.NotificationLog WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE {Claimable}
            ORDER BY NextAttemptAt
        )
        UPDATE claimable
        SET LockedUntil = DATEADD(second, {SendWorker.LeaseSeconds}, SYSDATETIMEOFFSET())
        OUTPUT inserted.NotificationId, inserted.EventId, inserted.CorrelationId, inserted.OrderId, inserted.CustomerId,
            inserted.TemplateKey, inserted.Parameters, inserted.CreatedAt, inserted.SendStartedAt,
            inserted.TemplateVersion, inserted.Languages;
        """;

    // The dispatcher's ladder, read from its constants so the two cannot drift; the lease drops with it. No count
    // abandons a row: waiting ends at DeliveryOptions.GiveUpAge (ADR-052).
    private static readonly string BackOffSql =
        $"""
        UPDATE notifications.NotificationLog
        SET
            Attempts      = Attempts + 1,
            LockedUntil   = NULL,
            NextAttemptAt = DATEADD(
                second,
                POWER(2, CASE WHEN Attempts > {OutboxDispatcher.BackoffAttemptCap}
                              THEN {OutboxDispatcher.BackoffAttemptCap}
                              ELSE Attempts END) * {OutboxDispatcher.BackoffBaseSeconds},
                SYSDATETIMEOFFSET())
        WHERE NotificationId = @NotificationId AND Status = 'Pending';
        """;

    public async Task<IReadOnlyList<SendWork>> ClaimAsync(CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        // CommandDefinition, so a shutdown's token can interrupt a blocked claim.
        return [.. await connection.QueryAsync<SendWork>(new CommandDefinition(ClaimSql, cancellationToken: ct))];
    }

    public async Task BackOffAsync(Guid notificationId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(
            new CommandDefinition(BackOffSql, new { NotificationId = notificationId }, cancellationToken: ct));
    }
}
