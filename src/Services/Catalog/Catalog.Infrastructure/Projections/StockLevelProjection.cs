using System.Data;
using Common.Application;
using Common.Contracts.Inventory.V1;
using Dapper;

namespace Catalog.Infrastructure.Projections;

/// <summary>A level with an OccurredAt watermark, so a stale or redelivered level changes nothing (§9.4).</summary>
/// <remarks>
/// Public, since §6.2's scan skips internal types; on its own connection, since §6.6 and §7.5 keep a
/// projection out of the write transaction.
/// </remarks>
public sealed class StockLevelProjection(IDbConnectionFactory connections)
    : IIntegrationEventHandler<StockLevelChanged>
{
    // HOLDLOCK, or two first deliveries both take NOT MATCHED; the guard is strict, as §6.6's price upsert's is.
    private const string UpsertSql =
        """
        MERGE catalog.StockLevels WITH (HOLDLOCK) AS target
        USING (SELECT ProductId = @ProductId) AS source
            ON target.ProductId = source.ProductId
        WHEN NOT MATCHED THEN
            INSERT (ProductId, QuantityAvailable, AsOf)
            VALUES (@ProductId, @QuantityAvailable, @OccurredAt)
        WHEN MATCHED AND target.AsOf < @OccurredAt THEN
            UPDATE SET QuantityAvailable = @QuantityAvailable, AsOf = @OccurredAt;
        """;

    public async Task HandleAsync(StockLevelChanged integrationEvent, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(
            UpsertSql,
            new { integrationEvent.ProductId, integrationEvent.QuantityAvailable, integrationEvent.OccurredAt },
            cancellationToken: ct));
    }
}
