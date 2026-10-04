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

    /// <summary>Every order the buyers placed is deleted here, which the 30 s default would not finish.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    public static async Task RunAsync(string connectionString, CancellationToken ct)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.ExecuteAsync(
            new CommandDefinition(
                Sql,
                new { Endpoint = Replay.Queue },
                commandTimeout: (int)CommandTimeout.TotalSeconds,
                cancellationToken: ct));
    }
}
