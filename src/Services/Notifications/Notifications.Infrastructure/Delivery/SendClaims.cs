using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Extensions.Options;
using Notifications.Application.Preferences;

namespace Notifications.Infrastructure.Delivery;

/// <summary>ADR-052's lease and backoff over <c>NotificationLog</c>, in <c>FulfilmentClaims</c>' shape.</summary>
internal sealed class SendClaims(IDbConnectionFactory connections, IOptions<SendPacingOptions> pacing)
{
    /// <summary>A pending row due and unleased; the first line repeats the claim index's filter.</summary>
    internal const string Claimable =
        """
        Status = 'Pending'
            AND NextAttemptAt <= SYSDATETIMEOFFSET()
            AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
        """;

    // One statement selects and leases, so two replicas cannot take one row; READPAST skips another's. An empty
    // CorrelationId is the column's default, left by a version that predates it while both run (§7.4), so the row's
    // order stands in, as AddNotificationCorrelationId's backfill argues. An open relay leaves the mail notices out
    // (the push design's section 1). The claim index is named: the pending-order index shares its filter, and reading
    // that one locks every pending row it looks up (ADR-175). SendPacingOptions sets the batch and the lease.
    private static readonly string ClaimSql =
        $"""
        WITH claimable AS (
            SELECT TOP (@BatchSize) *
            FROM notifications.NotificationLog WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(IX_NotificationLog_SendClaim))
            WHERE {Claimable}
                AND (@MailParked = 0 OR Channel <> '{ConsentVocabulary.Email}')
            ORDER BY NextAttemptAt
        )
        UPDATE claimable
        SET LockedUntil = DATEADD(second, @LeaseSeconds, SYSDATETIMEOFFSET())
        OUTPUT inserted.NotificationId, inserted.EventId,
            COALESCE(NULLIF(inserted.CorrelationId, '{Guid.Empty:D}'), inserted.OrderId) AS CorrelationId,
            inserted.OrderId, inserted.CustomerId, inserted.TemplateKey, inserted.Channel, inserted.Parameters,
            inserted.CreatedAt, inserted.SendStartedAt, inserted.TemplateVersion, inserted.Languages,
            inserted.TraceParent, inserted.TraceState, inserted.LockedUntil;
        """;

    // The dispatcher's ladder, read from its constants so the two cannot drift; the lease drops with it. No count
    // abandons a row: waiting ends at DeliveryOptions.GiveUpAge (ADR-052). Only under the lease the caller took, so a
    // pass that outlived it cannot drop a lease another replica has since taken, nor count an attempt for it.
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
        WHERE NotificationId = @NotificationId
            AND Status = 'Pending'
            AND LockedUntil = @Lease;
        """;

    /// <summary>Leases due notices; with <paramref name="mailParked"/>, push notices alone.</summary>
    public async Task<IReadOnlyList<SendWork>> ClaimAsync(bool mailParked, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();
        SendPacingOptions pace = pacing.Value;

        // CommandDefinition, so a shutdown's token can interrupt a blocked claim.
        return
        [
            .. await connection.QueryAsync<SendWork>(
                new CommandDefinition(
                    ClaimSql,
                    new
                    {
                        MailParked = mailParked,
                        BatchSize = pace.SendClaimBatchSize,
                        LeaseSeconds = pace.SendLeaseSeconds
                    },
                    cancellationToken: ct))
        ];
    }

    /// <summary>Backs the row off, if it is still under <paramref name="lease"/>; true when it was.</summary>
    public async Task<bool> BackOffAsync(Guid notificationId, DateTimeOffset lease, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return await connection.ExecuteAsync(
            new CommandDefinition(
                BackOffSql,
                new { NotificationId = notificationId, Lease = lease },
                cancellationToken: ct)) == 1;
    }
}
