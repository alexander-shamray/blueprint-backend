using System.Data;
using Common.Application;
using Common.Contracts.Inventory.V1;
using Dapper;

namespace Catalog.Infrastructure.Projections;

/// <summary>
/// A level, not a delta, with a watermark on OccurredAt: a stale or a
/// redelivered level changes nothing (§9.4).
/// </summary>
/// <remarks>
/// Public, and the modifier is load-bearing: §6.2's scan is public-only, so
/// an internal handler is registered as nothing at all while the endpoint
/// stays bound. Its own connection, never a consumer's <c>DbContext</c>:
/// §6.6 and §7.5 both keep a projection out of the write transaction.
/// </remarks>
public sealed class StockLevelProjection(IDbConnectionFactory connections)
    : IIntegrationEventHandler<StockLevelChanged>
{
    // HOLDLOCK is what keeps two deliveries for one new product from both
    // taking the NOT MATCHED branch: a bare MERGE takes no range lock over the
    // key it failed to find, and the second insert would violate the primary
    // key. The guard is strict, as §6.6's price upsert's is — a tie between
    // two levels has no business answer, so delivery order decides.
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
