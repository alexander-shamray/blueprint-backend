using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace Shipping.Infrastructure.Observability;

/// <summary>§13.6's <see cref="IOutboxStats"/> over three aggregate queries.</summary>
/// <remarks>
/// Cached briefly, since a metrics type that loads the database it measures causes the symptom. It throws;
/// <see cref="OutboxMetrics"/> contains that into an absent series.
/// </remarks>
internal sealed class OutboxStats : IOutboxStats, IDisposable
{
    /// <summary>Inside one export interval; per question and lane (§13.6), a damper rather than a lock.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>Not SqlClient's thirty: in gauge callbacks, a black-holed database would stall the reader.</summary>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>The open a command timeout does not cover, as the connection's <c>ConnectTimeout</c>.</summary>
    public const int ConnectTimeoutSeconds = 2;

    private readonly IDbConnectionFactory _connections;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly string _oldestSql;
    private readonly string _pendingSql;
    private readonly string _abandonedSql;

    public OutboxStats(IDbConnectionFactory connections, OutboxTable table)
    {
        _connections = connections;

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

        // The dispatcher's cap (§9.4), not a copy that could drift from the loop it describes.
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

    /// <summary><c>double</c> throughout: the age is one, and a count is widened for the gauge anyway.</summary>
    private double Read(string key, string sql, OutboxLane lane) =>
        _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            using IDbConnection connection = _connections.Create();

            // MIN over an empty lane is NULL; COUNT never is, so one coalesce serves both.
            return connection.ExecuteScalar<double?>(
                new CommandDefinition(
                    sql,
                    new { lane = lane.ToString() },
                    commandTimeout: CommandTimeoutSeconds)) ?? 0;
        });
}
