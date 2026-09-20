using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace Payments.Infrastructure.Observability;

/// <summary>
/// §13.6's <see cref="IOutboxStats"/> over three aggregate queries.
/// </summary>
/// <remarks>
/// It takes the connection factory rather than a scope, because that port is
/// a singleton (§6.5) holding a string. Cached briefly, because a metrics
/// type that loads the database it measures causes the symptom. It throws;
/// <see cref="OutboxMetrics"/> contains that into an absent series.
/// </remarks>
internal sealed class OutboxStats : IOutboxStats, IDisposable
{
    /// <summary>
    /// Short enough that a stalled lane is visible within one export interval,
    /// long enough that a burst of scrapes does not become a burst of queries.
    /// </summary>
    /// <remarks>
    /// Cached per question and lane, the shape §13.6 specifies, so a
    /// collection is six queries rather than one snapshot.
    /// <c>GetOrCreate</c> takes no lock, so two scrapes can both miss and both
    /// query: a damper rather than a guarantee.
    /// </remarks>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A bound on each statement, because these run inside observable gauge
    /// callbacks and the metric reader invokes them on its own thread.
    /// </summary>
    /// <remarks>
    /// Two seconds, not SqlClient's default of thirty. Against a black-holed
    /// database — a dropped route, a NetworkPolicy change — the default would
    /// let six callbacks' waits serialise into minutes and stall the reader,
    /// taking unrelated telemetry down with these gauges.
    /// </remarks>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>
    /// The half a command timeout does not cover:
    /// <c>AddPaymentsInfrastructure</c> builds this type's connection string
    /// with <c>ConnectTimeout</c> set to it.
    /// </summary>
    /// <remarks>
    /// A <c>commandTimeout</c> starts once a connection is open, so against a
    /// database that black-holes rather than refuses the open blocks first and
    /// the command timer never gets a chance.
    /// </remarks>
    public const int ConnectTimeoutSeconds = 2;

    private readonly IDbConnectionFactory _connections;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly string _oldestSql;
    private readonly string _pendingSql;
    private readonly string _abandonedSql;

    public OutboxStats(IDbConnectionFactory connections, OutboxTable table)
    {
        _connections = connections;

        // Composed from the registered table for the reason OutboxTable itself
        // gives: a second literal here would be a second place the schema has
        // to be right.
        _oldestSql =
            $"""
            SELECT DATEDIFF(second, MIN(OccurredAt), SYSDATETIMEOFFSET())
            FROM {table.QualifiedName}
            WHERE ProcessedAt IS NULL
                AND Lane = @lane;
            """;

        _pendingSql =
            $"""
            SELECT COUNT(*)
            FROM {table.QualifiedName}
            WHERE ProcessedAt IS NULL
                AND Lane = @lane;
            """;

        // The cap is read from the dispatcher rather than written again here.
        // §9.4 claims rows `WHERE Attempts < 10`, so a row at or above the cap
        // is skipped for ever — and a second copy of that number is a gauge
        // that stops agreeing with the loop it describes on the day somebody
        // tunes one of them.
        _abandonedSql =
            $"""
            SELECT COUNT(*)
            FROM {table.QualifiedName}
            WHERE ProcessedAt IS NULL
                AND Lane = @lane
                AND Attempts >= {OutboxDispatcher.MaxAttempts};
            """;
    }

    public double OldestAgeSeconds(OutboxLane lane) =>
        Read($"oldest:{lane}", _oldestSql, lane);

    public int PendingCount(OutboxLane lane) =>
        (int)Read($"pending:{lane}", _pendingSql, lane);

    public int AbandonedCount(OutboxLane lane) =>
        (int)Read($"abandoned:{lane}", _abandonedSql, lane);

    public void Dispose() => _cache.Dispose();

    /// <summary>
    /// One shape for all three, because they differ only in their statement.
    /// <c>double</c> throughout: the age is one, and a count that has to be
    /// widened for the gauge anyway loses nothing by being widened here.
    /// </summary>
    private double Read(string key, string sql, OutboxLane lane) =>
        _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            using IDbConnection connection = _connections.Create();

            // NULL rather than zero is what an empty lane returns from the age
            // query — MIN over no rows — and COUNT never returns it. One
            // coalesce covers both because the alternative is two helpers that
            // differ in a `??`.
            return connection.ExecuteScalar<double?>(
                new CommandDefinition(
                    sql,
                    new { lane = lane.ToString() },
                    commandTimeout: CommandTimeoutSeconds)) ?? 0;
        });
}
