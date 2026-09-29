using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace Shipping.Infrastructure.Observability;

/// <summary>
/// <see cref="IShipmentStats"/> over one aggregate query per state, run on a
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
    /// counts its own failures (ADR-054), so a Booked row whose cancellation
    /// keeps failing is counted while its polls succeed. <c>TerminalAt</c>
    /// bounds it: <c>Shipment.Cancel</c> voids a pending row without clearing
    /// <c>Attempts</c>, and a row nothing will claim again is not waiting.
    /// </summary>
    private const string WaitingSql =
        """
        SELECT COUNT(*)
        FROM shipping.Shipments
        WHERE Status = @Status
            AND (Attempts > 0 OR PollAttempts > 0)
            AND TerminalAt IS NULL;
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

    public void Dispose() => _cache.Dispose();
}
