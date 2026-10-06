using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace Web.Bff.Observability;

/// <summary><see cref="IProjectionStats"/>, cached briefly in Shipping's <c>ShipmentStats</c>' shape.</summary>
/// <remarks>It throws; <see cref="ProjectionMetrics"/> contains that into an absent series (§13.6).</remarks>
internal sealed class ProjectionStats(IDbConnectionFactory connections, TimeProvider clock)
    : IProjectionStats, IDisposable
{
    /// <summary>Inside one export interval, so a repeat callback within it reuses the result.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>Bounded, since a wait inside a gauge callback stalls every other callback in the pass.</summary>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>The open bounded too: SqlClient's default would stall the pass while SQL is unreachable.</summary>
    private const int ConnectTimeoutSeconds = 2;

    /// <summary>Over the filtered index <c>IndexUnattributedOrders</c> adds, so it reads unowned rows alone.</summary>
    private const string OldestSql =
        """
        SELECT MIN(FirstSeenAt)
        FROM bff.Orders
        WHERE CustomerId IS NULL;
        """;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    // The registered clock, which stamped FirstSeenAt, rather than the engine's.
    public double UnattributedAgeSeconds() =>
        _cache.GetOrCreate(
            nameof(UnattributedAgeSeconds),
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheFor;
                using IDbConnection connection = connections.Create();
                connection.ConnectionString =
                    new SqlConnectionStringBuilder(connection.ConnectionString)
                    {
                        ConnectTimeout = ConnectTimeoutSeconds
                    }.ConnectionString;

                DateTimeOffset? oldest = connection.ExecuteScalar<DateTimeOffset?>(
                    new CommandDefinition(OldestSql, commandTimeout: CommandTimeoutSeconds));

                return oldest is null ? 0 : Math.Max(0, (clock.GetUtcNow() - oldest.Value).TotalSeconds);
            });

    public void Dispose() => _cache.Dispose();
}
