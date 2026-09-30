using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Tracking;

namespace Shipping.Infrastructure.Observability;

/// <summary><see cref="IShipmentStats"/>, cached briefly in <see cref="OutboxStats"/>' shape.</summary>
/// <remarks>It throws; <see cref="ShipmentMetrics"/> contains that into an absent series (§13.6).</remarks>
internal sealed class ShipmentStats(IDbConnectionFactory connections) : IShipmentStats, IDisposable
{
    /// <summary>Inside one export interval, so a repeat callback within it reuses the result.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>Bounded, since a wait inside a gauge callback stalls every other callback in the pass.</summary>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>Live rows past a worker's first failed pass (ADR-054); <c>Attempts</c> only if claimable.</summary>
    private const string WaitingSql =
        """
        SELECT COUNT(*)
        FROM shipping.Shipments
        WHERE Status = @Status
            AND TerminalAt IS NULL
            AND (PollAttempts > 0
                 OR (Attempts > 0
                     AND Status IN ('Pending', 'Booked')
                     AND CancellationRefusedAt IS NULL
                     AND (Status = 'Pending' OR CancellationRequestedAt IS NOT NULL)));
        """;

    // Each claim's own population, aged from when each row became due; NULL is no wait. Shipment.Cancel stamps
    // only CancellationRequestedAt, so a Booked row is due at the later of the two.
    private static readonly string FulfilmentOverdueSql =
        $"""
        SELECT DATEDIFF_BIG(
            millisecond,
            MIN(CASE WHEN CancellationRequestedAt > NextAttemptAt THEN CancellationRequestedAt ELSE NextAttemptAt END),
            SYSDATETIMEOFFSET()) / 1000.0
        FROM shipping.Shipments
        WHERE {FulfilmentClaims.Claimable};
        """;

    private static readonly string TrackingOverdueSql =
        $"""
        SELECT DATEDIFF_BIG(millisecond, MIN(NextPollAt), SYSDATETIMEOFFSET()) / 1000.0
        FROM shipping.Shipments
        WHERE {TrackingClaims.Claimable};
        """;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public int WaitingCount(string state) =>
        _cache.GetOrCreate(state, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            using IDbConnection connection = connections.Create();

            return connection.ExecuteScalar<int>(
                new CommandDefinition(
                    WaitingSql,
                    new { Status = state },
                    commandTimeout: CommandTimeoutSeconds));
        });

    public double FulfilmentOverdueSeconds() => OverdueSeconds(FulfilmentOverdueSql);

    public double TrackingOverdueSeconds() => OverdueSeconds(TrackingOverdueSql);

    // Keyed by the statement, which no state's name equals; floored, as host-stamped instants meet the engine's clock.
    private double OverdueSeconds(string sql) =>
        _cache.GetOrCreate(sql, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            using IDbConnection connection = connections.Create();

            return Math.Max(
                0,
                connection.ExecuteScalar<double?>(
                    new CommandDefinition(sql, commandTimeout: CommandTimeoutSeconds)) ?? 0);
        });

    public void Dispose() => _cache.Dispose();
}
