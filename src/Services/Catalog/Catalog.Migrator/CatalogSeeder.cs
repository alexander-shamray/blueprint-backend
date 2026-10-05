using Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Catalog.Migrator;

/// <summary>§14.3's development catalogue, each product written with the outbox row its publish would stage.</summary>
/// <remarks>
/// SQL through the migration's own context, since §4.2 keeps the migrator off Application and Domain; the ids are
/// the ones deploy/compose/README.md publishes, which Inventory's seeder stocks.
/// </remarks>
public sealed class CatalogSeeder(CatalogDbContext db, ILogger<CatalogSeeder> logger)
{
    // The persisted name MessageTypeMap gives V1.ProductPublished (§9.4), whose assembly the migrator cannot see.
    private const string ProductPublished = "Common.Contracts.Catalog.V1.ProductPublished";

    private const string Currency = "EUR";

    private static readonly Action<ILogger, int, int, Exception?> Seeded =
        LoggerMessage.Define<int, int>(
            LogLevel.Information,
            new EventId(1, nameof(Seeded)),
            "Seeded {Published} of {Total} products; the rest were already present.");

    // Tuples rather than a record, whose synthesised equality would reach past §4.2's allow-list.
    private static readonly (int Ordinal, string Name, decimal Amount)[] Products =
    [
        (1, "Oak bookshelf", 189.00m),
        (2, "Linen cushion", 24.50m),
        (3, "Ceramic mug", 9.90m),
        (4, "Desk lamp", 39.00m),
        (5, "Wool throw", 59.00m),
        (6, "Cast-iron pan", 45.00m),
        (7, "Glass carafe", 18.00m),
        (8, "A5 notebook", 6.50m),
        (9, "Fountain pen", 32.00m),
        (10, "Walnut tray", 27.00m),
        (11, "Steel kettle", 49.00m),
        (12, "Cotton apron", 16.00m),
        (13, "Bamboo chopping board", 21.00m),
        (14, "Stoneware bowl", 12.00m),
        (15, "Wall clock", 35.00m),
        (16, "Terracotta plant pot", 14.00m),
        (17, "Reading chair", 320.00m),
        (18, "Side table", 110.00m),
        (19, "Jute rug", 140.00m),
        (20, "Candle set", 19.00m),
        (21, "Picture frame", 15.00m),
        (22, "Coat hook", 8.00m),
        (23, "Storage basket", 26.00m),
        (24, "Door mat", 22.00m)
    ];

    public async Task SeedAsync(CancellationToken ct)
    {
        // The run's own clock rather than a fixed stamp, so §13.7's lag measures a delivery and not the seed's age.
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();

        // One transaction, retried whole, so no product commits without the row that announces it (§9.4).
        int published = await strategy.ExecuteAsync(
            async token =>
            {
                await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(token);
                int count = 0;

                foreach ((int Ordinal, string Name, decimal Amount) product in Products)
                    count += await PublishAsync(product, now, token);

                await transaction.CommitAsync(token);
                return count;
            },
            ct);

        Seeded(logger, published, Products.Length, null);
    }

    /// <returns>1 if the product was written, 0 if a row already held its id.</returns>
    private async Task<int> PublishAsync(
        (int Ordinal, string Name, decimal Amount) product,
        DateTimeOffset now,
        CancellationToken ct)
    {
        Guid id = SeedId("5eed0000", product.Ordinal);
        Guid messageId = SeedId("5eed0001", product.Ordinal);

        // A millisecond apart, so the listing's newest-first order is the table's order reversed (§6.5).
        DateTimeOffset publishedAt = now.AddMilliseconds(product.Ordinal);

        int inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO catalog.Products (Id, Name, ThumbnailUrl, PriceAmount, PriceCurrency, PublishedAt)
            SELECT {id}, {product.Name}, NULL, {product.Amount}, {Currency}, {publishedAt}
            WHERE NOT EXISTS (SELECT 1 FROM catalog.Products WHERE Id = {id});
            """,
            ct);

        // A row already there is kept as it is, and staging again would announce a product twice.
        if (inserted == 0)
            return 0;

        // The payload CatalogIntegrationEventMapper maps and OutboxJson writes, serialised here by FOR JSON instead.
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO catalog.OutboxMessages
                (MessageId, CorrelationId, MessageType, Payload, Lane, OccurredAt, Attempts)
            SELECT
                {messageId},
                {id},
                {ProductPublished},
                (
                    SELECT
                        MessageId     = {messageId},
                        CorrelationId = {id},
                        OccurredAt    = {publishedAt},
                        ProductId     = {id},
                        Name          = {product.Name},
                        ThumbnailUrl  = NULL,
                        Amount        = {product.Amount},
                        Currency      = {Currency}
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
                ),
                'Broker',
                {publishedAt},
                0;
            """,
            ct);

        return 1;
    }

    // The ordinal in decimal digits, so an id in the README and its row in the table read alike.
    private static Guid SeedId(string prefix, int ordinal) => Guid.Parse($"{prefix}-0000-0000-0000-{ordinal:D12}");
}
