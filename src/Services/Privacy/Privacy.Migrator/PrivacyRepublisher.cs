using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Privacy.Infrastructure.Persistence;

namespace Privacy.Migrator;

/// <summary>ADR-090's republish for Privacy: each close it may announce, staged on the Broker lane (ADR-142).</summary>
/// <remarks>
/// SQL through the migration's own context, since §4.2 keeps the migrator off Application and Domain. The payload
/// keeps the close as <c>OccurredAt</c>, which holders stamp as <c>ClosedAt</c> (ADR-139), under a fresh id (§9.5).
/// </remarks>
public sealed class PrivacyRepublisher(
    PrivacyDbContext db,
    ILogger<PrivacyRepublisher> logger,
    RepublishRequest request)
{
    // The persisted name MessageTypeMap gives the V1 contract (§9.4), whose assembly the migrator cannot see.
    private const string DeleteClosed = "Common.Contracts.Privacy.V1.PersonalDataDeleteClosed";

    private static readonly Action<ILogger, int, Exception?> Staged =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1, nameof(Staged)),
            "Republished {Requests} closed request(s); the dispatcher delivers them from here.");

    private static readonly Action<ILogger, Guid, Exception?> NotAnnounceable =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(2, nameof(NotAnnounceable)),
            "No closed request {RequestId} that may be announced: it does not exist, is still open, or closed by " +
            "an amendment that removed a holder never heard from (ADR-139). The job exits non-zero.");

    private static readonly Action<ILogger, Exception?> Failed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(3, nameof(Failed)),
            "The republish failed and staged nothing. The job exits non-zero.");

    /// <returns>0 once every matching close is staged; 1 if the id matched none, or the run threw.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        // The run's own clock for the rows, so §13.7's age measures this backlog and not the requests' ages.
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        bool all = request.Id is null;
        Guid id = request.Id ?? Guid.Empty;

        try
        {
            // One statement, so it stages every close or none, and the execution strategy may retry it whole.
            int requests = await db.Database.CreateExecutionStrategy().ExecuteAsync(
                token => StageClosedAsync(all, id, now, token),
                ct);

            if (requests == 0 && !all)
            {
                NotAnnounceable(logger, id, null);
                return 1;
            }

            Staged(logger, requests, null);
            return 0;
        }
        catch (Exception ex)
        {
            // Broad on purpose: an escaped exception would exit without the sentence an operator needs.
            Failed(logger, ex);
            return 1;
        }
    }

    // The aggregate's own test for a close it may publish, read from the rows it left: no amendment removed a
    // holder that no completion was heard from, since a closed request records no later answer (ADR-139).
    private Task<int> StageClosedAsync(bool all, Guid id, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO privacy.OutboxMessages
                (MessageId, CorrelationId, MessageType, Payload, Lane, OccurredAt, Attempts)
            SELECT
                r.MessageId,
                r.RequestId,
                {DeleteClosed},
                (
                    SELECT
                        MessageId     = r.MessageId,
                        CorrelationId = r.RequestId,
                        OccurredAt    = r.ClosedAt,
                        RequestId     = r.RequestId
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
                ),
                'Broker',
                {now},
                0
            FROM (
                SELECT MessageId = NEWID(), request.RequestId, request.ClosedAt
                FROM privacy.ErasureRequests request
                WHERE ({all} = 1 OR request.RequestId = {id})
                    AND request.Status = 'Closed'
                    AND request.ClosedAt IS NOT NULL
                    AND NOT EXISTS (
                        SELECT 1
                        FROM privacy.ErasureAmendments amendment
                        CROSS APPLY STRING_SPLIT(amendment.Removed, ',') removed
                        WHERE amendment.RequestId = request.RequestId
                            AND removed.value <> ''
                            AND NOT EXISTS (
                                SELECT 1
                                FROM privacy.ErasureCompletions heard
                                WHERE heard.RequestId = request.RequestId
                                    AND heard.Responder = removed.value))
            ) AS r;
            """,
            ct);
}
