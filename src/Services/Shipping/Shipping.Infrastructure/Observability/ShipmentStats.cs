using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Tracking;

namespace Shipping.Infrastructure.Observability;

/// <summary>
/// <see cref="IShipmentStats"/> over one aggregate query per state or pass, run on a
/// cache miss, in <c>OutboxStats</c>' shape and on its arguments: a connection
/// factory rather than a scope, a bounded command timeout, and a short cache,
/// because a metrics type that loads the database it measures is a monitor
/// that causes the symptom. A read that throws surfaces as an absent series,
/// which <c>ShipmentMetrics.PerState</c> is what makes true.
/// </summary>
internal sealed class ShipmentStats(IDbConnectionFactory connections) : IShipmentStats, IDisposable
{
    /// <summary>
    /// Short enough that a stalled state is visible within one export interval,
    /// long enough that a burst of scrapes is not a burst of queries.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A bound on the statement, because this runs inside a gauge callback on
    /// the metric reader's own thread: a wait here serialises with every other
    /// callback in the pass and takes unrelated telemetry down with it.
    /// </summary>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>
    /// Live rows past their first failed pass, whichever worker took it: each
    /// counts its own failures (ADR-054). A row nothing will claim again is not
    /// waiting, so <c>TerminalAt</c> bounds both counts and <c>Attempts</c> is
    /// read only on the rows <c>FulfilmentClaims</c> selects: a despatch leaves
    /// the count a failed cancel wrote, and no fulfilment pass will clear it.
    /// </summary>
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

    // Each claim's own population, read from the claim, over its own due
    // column: a row that is due and that no pass holds is one a replica has
    // not reached, so the oldest one's wait is how far the passes are behind.
    // NULL when there is none, which is no wait at all.
    private static readonly string FulfilmentOverdueSql =
        $"""
        SELECT DATEDIFF_BIG(millisecond, MIN(NextAttemptAt), SYSDATETIMEOFFSET()) / 1000.0
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

    // Keyed by the statement, which no state's name can equal.
    private double OverdueSeconds(string sql) =>
        _cache.GetOrCreate(sql, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            using IDbConnection connection = connections.Create();

            return connection.ExecuteScalar<double?>(
                new CommandDefinition(sql, commandTimeout: CommandTimeoutSeconds)) ?? 0;
        });

    public void Dispose() => _cache.Dispose();
}
