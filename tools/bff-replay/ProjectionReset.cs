using Common.Infrastructure.Inbox;
using Dapper;
using Microsoft.Data.SqlClient;
using Web.Bff.Persistence;

namespace BffReplay;

/// <summary>Step three of the rebuild: the order rows and the queue's inbox rows, deleted in one transaction.</summary>
/// <remarks>
/// <c>bff.Products</c> survives: a name is published once and usually predates every outbox window, and its upsert
/// guards on <c>OccurredAt</c>, so a replayed <c>ProductPublished</c> over a kept row is harmless (ADR-051, §6.6).
/// </remarks>
public static class ProjectionReset
{
    /// <summary>Reads no row, and fails on a missing table or grant: the preflight's question of the BFF.</summary>
    public static readonly string ProbeSql =
        $"SELECT TOP (0) OrderId FROM [{BffSchema.Name}].Orders; " +
        $"SELECT TOP (0) MessageId FROM {new InboxTable(BffSchema.Name).QualifiedName};";

    /// <summary>Lines first, so the delete is complete without leaning on the foreign key's cascade.</summary>
    public static readonly string Sql =
        $"""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        DELETE FROM [{BffSchema.Name}].OrderLines;
        DELETE FROM [{BffSchema.Name}].Orders;
        DELETE FROM {new InboxTable(BffSchema.Name).QualifiedName} WHERE Endpoint = @Endpoint;

        COMMIT;
        """;

    /// <summary>Every order the buyers placed is deleted here, which the 30 s default might not finish.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The key <c>InboxFilter</c> writes: the receive address's path, which a named vhost prefixes.</summary>
    public static string EndpointFor(string broker, string queue)
    {
        string vhost = new Uri(broker).AbsolutePath.Trim('/');

        // %2F is AMQP's spelling of the default vhost, which MassTransit's address leaves out.
        return vhost.Length == 0 || vhost.Equals("%2F", StringComparison.OrdinalIgnoreCase)
            ? queue
            : $"{vhost}/{queue}";
    }

    /// <summary>The ids the queue's inbox holds, in one read, so a repair can leave them unsent.</summary>
    public static async Task<HashSet<Guid>> HandledAsync(string connectionString, string broker, CancellationToken ct)
    {
        await using SqlConnection connection = new(connectionString);

        return
        [
            .. await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    $"SELECT MessageId FROM {new InboxTable(BffSchema.Name).QualifiedName} WHERE Endpoint = @Endpoint;",
                    new { Endpoint = EndpointFor(broker, Replay.Queue) },
                    cancellationToken: ct))
        ];
    }

    public static async Task RunAsync(string connectionString, string broker, CancellationToken ct)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.ExecuteAsync(
            new CommandDefinition(
                Sql,
                new { Endpoint = EndpointFor(broker, Replay.Queue) },
                commandTimeout: (int)CommandTimeout.TotalSeconds,
                cancellationToken: ct));
    }
}
