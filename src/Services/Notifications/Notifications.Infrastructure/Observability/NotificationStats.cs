using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Notifications.Application.Preferences;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Mail;

namespace Notifications.Infrastructure.Observability;

/// <summary><see cref="INotificationStats"/>, cached briefly in <c>ShipmentStats</c>' shape.</summary>
/// <remarks>It throws; <see cref="NotificationMetrics"/> contains that into an absent series (§13.6).</remarks>
internal sealed class NotificationStats(IDbConnectionFactory connections) : INotificationStats, IDisposable
{
    /// <summary>The open a command timeout does not cover, as the connection's <c>ConnectTimeout</c>.</summary>
    public const int ConnectTimeoutSeconds = 2;

    /// <summary>Not SqlClient's thirty: in a gauge callback, a black-holed database would stall the reader.</summary>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>Inside one export interval, so a repeat callback within it reuses the result.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>Two ticks: a due row a healthy pass has not reached yet reads as no wait.</summary>
    internal static readonly TimeSpan OverdueGrace = MailHop.SendTick * 2;

    // The intent first, then the record, then everything else before the intent: a push notice's devices, a mail
    // notice's contact. A delivery waits on its platform's provider (the push design's section 4).
    private static readonly string WaitingSql =
        $"""
        SELECT Step, Waiting = COUNT(*)
        FROM (
            SELECT Step = CASE
                WHEN n.SendStartedAt IS NOT NULL THEN '{WaitingSteps.Relay}'
                WHEN r.OrderId IS NULL
                    OR (n.TemplateKey = '{TemplateKeys.PaymentDeclined}' AND r.CancelledAt IS NULL)
                    THEN '{WaitingSteps.OrderRecord}'
                WHEN n.Channel = '{ConsentVocabulary.Push}' THEN '{WaitingSteps.Devices}'
                ELSE '{WaitingSteps.Contact}'
            END
            FROM notifications.NotificationLog n
            LEFT JOIN notifications.OrderRecords r
                ON r.OrderId = n.OrderId
            WHERE n.Status = 'Pending'
                AND n.Attempts > 0
            UNION ALL
            SELECT CASE d.Platform
                WHEN '{WaitingSteps.Fcm}' THEN '{WaitingSteps.Fcm}'
                ELSE '{WaitingSteps.Apns}'
            END
            FROM notifications.PushDeliveries d
            WHERE d.Status = 'Pending'
                AND d.Attempts > 0
        ) waiting
        GROUP BY Step;
        """;

    // Both claims' own populations, aged from when each row became due; NULL is no wait.
    private static readonly string OverdueSql =
        $"""
        SELECT DATEDIFF_BIG(millisecond, MIN(Due), SYSDATETIMEOFFSET()) / 1000.0
        FROM (
            SELECT Due = NextAttemptAt FROM notifications.NotificationLog WHERE {SendClaims.Claimable}
            UNION ALL
            SELECT NextAttemptAt FROM notifications.PushDeliveries WHERE {PushClaims.Claimable}
        ) due;
        """;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public IReadOnlyDictionary<string, int> WaitingByStep() =>
        _cache.GetOrCreate(
            nameof(WaitingByStep),
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheFor;
                using IDbConnection connection = connections.Create();

                Dictionary<string, int> waiting = WaitingSteps.All.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
                CommandDefinition query = new(WaitingSql, commandTimeout: CommandTimeoutSeconds);
                foreach (WaitingRow row in connection.Query<WaitingRow>(query))
                {
                    waiting[row.Step] = row.Waiting;
                }

                return waiting;
            })!;

    // Floored, as host-stamped instants meet the engine's clock, and less the grace a healthy pass needs.
    public double OverdueSeconds() =>
        _cache.GetOrCreate(
            nameof(OverdueSeconds),
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheFor;
                using IDbConnection connection = connections.Create();

                double? waited = connection.ExecuteScalar<double?>(
                    new CommandDefinition(OverdueSql, commandTimeout: CommandTimeoutSeconds));

                return Math.Max(0, (waited ?? 0) - OverdueGrace.TotalSeconds);
            });

    public void Dispose() => _cache.Dispose();

    private sealed record WaitingRow(string Step, int Waiting);
}
