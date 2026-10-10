using System.Data;
using Common.Application;
using Dapper;

namespace Privacy.Infrastructure.Observability;

/// <summary><see cref="IErasureStats"/> over two aggregate queries, each bounded by a command timeout.</summary>
/// <remarks>It throws, and <see cref="ErasureMetrics"/> contains that into an absent series (§13.6).</remarks>
internal sealed class ErasureStats(IDbConnectionFactory connections) : IErasureStats
{
    /// <summary>Bounds each statement, because gauge callbacks run on the metric reader's own thread.</summary>
    private const int CommandTimeoutSeconds = 2;

    private const string OverdueSql = "SELECT COUNT(*) FROM privacy.ErasureRequests WHERE Status = 'Overdue';";

    // A holder counted in the set the request was raised with and heard from not at all, or only as a stray.
    private const string MissingSql =
        """
        SELECT Responder = holder.value, Requests = COUNT(*)
        FROM privacy.ErasureRequests request
        CROSS APPLY STRING_SPLIT(request.RespondersCsv, ',') holder
        WHERE request.Status = 'Overdue'
            AND NOT EXISTS (
                SELECT 1
                FROM privacy.ErasureCompletions answer
                WHERE answer.RequestId = request.RequestId
                    AND answer.Responder = holder.value
                    AND answer.Counted = 1)
        GROUP BY holder.value;
        """;

    public int OverdueCount()
    {
        using IDbConnection connection = connections.Create();

        return connection.ExecuteScalar<int>(OverdueSql, commandTimeout: CommandTimeoutSeconds);
    }

    public IReadOnlyDictionary<string, int> OverdueMissingByResponder()
    {
        using IDbConnection connection = connections.Create();

        return connection
            .Query<(string Responder, int Requests)>(MissingSql, commandTimeout: CommandTimeoutSeconds)
            .ToDictionary(row => row.Responder, row => row.Requests);
    }
}
