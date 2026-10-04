using System.Data;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Dapper;
using Web.Bff.Persistence;

namespace Web.Bff.Orders;

/// <summary>The names a line resolves on read (§10.7), from Catalog's <c>ProductPublished</c>.</summary>
/// <remarks>Public, because §6.2's scan is public-only.</remarks>
public sealed class ProductNameProjection(IDbConnectionFactory connections, ILogger<ProductNameProjection> log)
    : IIntegrationEventHandler<ProductPublished>
{
    /// <summary>Guarded on <c>OccurredAt</c>, sound here alone: Catalog's one clock mints them (§10.7).</summary>
    private const string UpsertSql =
        """
        MERGE bff.Products WITH (HOLDLOCK) AS target
        USING (SELECT ProductId = @ProductId) AS source
            ON target.ProductId = source.ProductId
        WHEN NOT MATCHED THEN
            INSERT (ProductId, Name, PublishedAt)
            VALUES (@ProductId, @Name, @OccurredAt)
        WHEN MATCHED AND target.PublishedAt < @OccurredAt THEN
            UPDATE SET Name = @Name, PublishedAt = @OccurredAt;
        """;

    // CA1848 (ADR-019).
    private static readonly Action<ILogger, Guid, int, Exception?> NameDropped =
        LoggerMessage.Define<Guid, int>(
            LogLevel.Warning,
            new EventId(1, nameof(NameDropped)),
            "Product {ProductId}'s name is blank or longer than its column's {Width} characters; not written.");

    public async Task HandleAsync(ProductPublished integrationEvent, CancellationToken ct)
    {
        // A line with no name reads productName null (§10.7), which beats an endpoint stalled on one message.
        if (string.IsNullOrWhiteSpace(integrationEvent.Name) ||
            integrationEvent.Name.Length > ProjectionLimits.ProductNameMaxLength)
        {
            NameDropped(log, integrationEvent.ProductId, ProjectionLimits.ProductNameMaxLength, null);
            return;
        }

        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(
            new CommandDefinition(
                UpsertSql,
                new { integrationEvent.ProductId, integrationEvent.Name, integrationEvent.OccurredAt },
                cancellationToken: ct));
    }
}
