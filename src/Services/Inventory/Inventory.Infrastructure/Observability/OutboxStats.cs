using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace Inventory.Infrastructure.Observability;

/// <summary>
/// §13.6's <see cref="IOutboxStats"/> over three aggregate queries.
/// </summary>
/// <remarks>
/// <b>It takes the connection factory rather than a scope, because that port is
/// a singleton (§6.5) holding a string.</b> §13.6's sample reached for an
/// <c>IServiceScopeFactory</c> on the stated grounds that this type must not
/// hold a <c>DbContext</c> — true, and satisfied here by never asking for one:
/// the reads are Dapper on a connection the caller disposes, which is what
/// §6.5 already says the read side is. The chapter was amended to match.
/// <para>
/// <b>Cached briefly, because the collector's schedule is not this type's to
/// choose.</b> An observable gauge is read once per export interval per
/// instrument, so the six callbacks would otherwise be six aggregate queries
/// every interval — and a metrics type that loads the database it is measuring
/// is a monitor that causes the symptom.
/// </para>
/// <para>
/// <b>Failure surfaces as an absent series, and <see cref="OutboxMetrics"/> is
/// what makes that true.</b> This type throws — a timeout or an unreachable
/// server is a <c>SqlException</c> like any other. An earlier comment here
/// claimed the SDK swallowed it; it does not, and
/// <c>MeterListener.RecordObservableInstruments</c> abandons the rest of the
/// pass, so the containment lives in the callback rather than in an assumption
/// about the collector. Nothing here tries to be clever about a database that
/// is down: readiness (§13.5) already covers that, and an outbox alert firing
/// because SQL Server is unreachable would page the wrong person with the
/// wrong runbook.
/// </para>
/// </remarks>
internal sealed class OutboxStats : IOutboxStats, IDisposable
{
    /// <summary>
    /// Short enough that a stalled lane is visible within one export interval,
    /// long enough that a burst of scrapes does not become a burst of queries.
    /// </summary>
    /// <remarks>
    /// <b>Each instrument is cached under its own key, so this is three
    /// statements per lane and six per collection — not one shared snapshot.</b>
    /// An earlier comment here claimed the three shared a round trip; they do
    /// not, and the entry is per <c>(question, lane)</c> because that is the
    /// shape §13.6 specifies. The cost is six aggregate queries over a filtered
    /// index per export interval, which at the SDK's default of sixty seconds
    /// is six a minute — small enough that collapsing them into one grouped
    /// query would be an optimisation rather than a fix, and large enough that
    /// removing the cache would not be.
    /// <para>
    /// <c>GetOrCreate</c> takes no lock, so two concurrent scrapes can both
    /// miss and both query. Harmless for a read, and worth knowing before
    /// anyone reads this cache as a guarantee rather than a damper.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A bound on each statement, because these run <b>inside observable gauge
    /// callbacks</b> and the metric reader invokes them on its own thread.
    /// </summary>
    /// <remarks>
    /// <b>Two seconds, not SqlClient's default of thirty.</b> Collection drives
    /// six callbacks; against a black-holed database — a dropped route or a
    /// NetworkPolicy change, where connections hang rather than refuse — the
    /// default would let those waits serialise into minutes and stall the
    /// reader, taking down <em>unrelated</em> telemetry with these gauges. That
    /// is a monitor causing an outage in the signal it exists to provide.
    /// <para>
    /// A timeout here throws, and <c>OutboxMetrics.PerLane</c> is what
    /// contains it: the measurement is skipped and the series is absent for
    /// that interval rather than wrong. Absent is the correct reading —
    /// readiness (§13.5) is what reports a database that is gone, and an
    /// outbox alert firing because SQL Server is unreachable would page the
    /// wrong person with the wrong runbook.
    /// </para>
    /// </remarks>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>
    /// The other half of that bound, and the half a command timeout does not
    /// cover: <c>AddInventoryInfrastructure</c> builds this type's connection
    /// string with <c>ConnectTimeout</c> set to it.
    /// </summary>
    /// <remarks>
    /// <b>A <c>commandTimeout</c> starts once a connection is open.</b>
    /// SqlClient's default connect timeout is fifteen seconds, so against a
    /// database that black-holes rather than refuses, the open blocks first and
    /// the command timer never gets a chance — the safeguard above would be
    /// stated and absent, which is the failure this whole pull request keeps
    /// finding in other people's guards.
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
        // gives: §13.6 writes `ordering.OutboxMessages` because it is a chapter
        // about Ordering, and a second literal here would be a second place the
        // schema has to be right.
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
