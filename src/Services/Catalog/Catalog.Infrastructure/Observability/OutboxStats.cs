using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace Catalog.Infrastructure.Observability;

/// <summary>§13.6's <see cref="IOutboxStats"/> over three aggregate queries, briefly cached.</summary>
/// <remarks>It throws, and <see cref="OutboxMetrics"/> contains that into an absent series.</remarks>
internal sealed class OutboxStats : IOutboxStats, IDisposable
{
    /// <summary>Shows a stall within one export interval, yet damps a burst of scrapes.</summary>
    /// <remarks>Per question and lane, as §13.6 specifies; <c>GetOrCreate</c> takes no lock.</remarks>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>Bounds each statement, because gauge callbacks run on the metric reader's own thread.</summary>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>The open, which a command timeout does not bound; <c>AddCatalogInfrastructure</c> applies it.</summary>
    public const int ConnectTimeoutSeconds = 2;

    private readonly IDbConnectionFactory _connections;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly string _oldestSql;
    private readonly string _pendingSql;
    private readonly string _abandonedSql;

    public OutboxStats(IDbConnectionFactory connections, OutboxTable table)
    {
        _connections = connections;

        // Composed from the registered table, for the reason OutboxTable gives.
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

        // The dispatcher's own cap, so this gauge cannot disagree with §9.4's claim loop.
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

    /// <summary>One shape for all three; <c>double</c>, because the gauge widens a count anyway.</summary>
    private double Read(string key, string sql, OutboxLane lane) =>
        _cache.GetOrCreate(
            key,
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheFor;
                using IDbConnection connection = _connections.Create();

                // MIN over an empty lane is NULL, and COUNT never is, so one coalesce serves all three.
                return connection.ExecuteScalar<double?>(
                    new CommandDefinition(
                        sql,
                        new { lane = lane.ToString() },
                        commandTimeout: CommandTimeoutSeconds)) ?? 0;
            });
}
