using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Inventory.Migrator;

/// <summary>§14.3's development stock, each count written with the outbox row its <c>SetOnHand</c> would stage.</summary>
/// <remarks>
/// Directly, since stock reaches Inventory by the admin path (§10.2) and from no event (§3.2); in SQL, since §4.2
/// keeps the migrator off Application and Domain. The ids are Catalog's seeded ones, which deploy/compose/README.md
/// publishes.
/// </remarks>
public sealed class InventorySeeder(InventoryDbContext db, ILogger<InventorySeeder> logger)
{
    // The persisted name MessageTypeMap gives V1.StockLevelChanged (§9.4), whose assembly the migrator cannot see.
    private const string StockLevelChanged = "Common.Contracts.Inventory.V1.StockLevelChanged";

    private static readonly Action<ILogger, int, int, Exception?> Seeded =
        LoggerMessage.Define<int, int>(
            LogLevel.Information,
            new EventId(1, nameof(Seeded)),
            "Seeded stock for {Stocked} of {Total} products; the rest were already present.");

    // Tuples rather than a record, whose synthesised equality would reach past §4.2's allow-list.
    private static readonly (int Ordinal, int OnHand)[] Stock =
    [
        (1, 40),
        (2, 120),
        (3, 500),
        (4, 75),
        (5, 60),
        (6, 80),
        (7, 150),
        (8, 400),
        (9, 90),
        (10, 70),
        (11, 55),
        (12, 200),
        (13, 110),
        (14, 300),
        (15, 45),
        (16, 250),
        (17, 2),
        (18, 25),
        (19, 15),
        (20, 180),
        (21, 220),
        (22, 350),
        (23, 130),
        (24, 0)
    ];

    public async Task SeedAsync(CancellationToken ct)
    {
        // The run's own clock rather than a fixed stamp, so §13.7's lag measures a delivery and not the seed's age.
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();

        // One transaction, retried whole, so no count commits without the row that announces it (§9.4).
        int stocked = await strategy.ExecuteAsync(
            async token =>
            {
                await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(token);
                int count = 0;

                foreach ((int Ordinal, int OnHand) item in Stock)
                    count += await StockAsync(item, now, token);

                await transaction.CommitAsync(token);
                return count;
            },
            ct);

        Seeded(logger, stocked, Stock.Length, null);
    }

    /// <returns>1 if the count was written, 0 if a row already held the product.</returns>
    private async Task<int> StockAsync((int Ordinal, int OnHand) item, DateTimeOffset now, CancellationToken ct)
    {
        Guid productId = SeedId("5eed0000", item.Ordinal);
        Guid messageId = SeedId("5eed0002", item.Ordinal);

        // Nothing reserved, so the whole count is available, which is what SetOnHand leaves on a new row.
        int inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt)
            SELECT {productId}, {item.OnHand}, 0, {now}
            WHERE NOT EXISTS (SELECT 1 FROM inventory.StockItems WHERE ProductId = {productId});
            """,
            ct);

        // A row already there is kept as it is, and staging again would announce a level it may no longer hold.
        if (inserted == 0)
            return 0;

        // The payload InventoryIntegrationEventMapper maps and OutboxJson writes, serialised here by FOR JSON instead.
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO inventory.OutboxMessages
                (MessageId, CorrelationId, MessageType, Payload, Lane, OccurredAt, Attempts)
            SELECT
                {messageId},
                {productId},
                {StockLevelChanged},
                (
                    SELECT
                        MessageId         = {messageId},
                        CorrelationId     = {productId},
                        OccurredAt        = {now},
                        ProductId         = {productId},
                        QuantityAvailable = {item.OnHand}
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
                ),
                'Broker',
                {now},
                0;
            """,
            ct);

        return 1;
    }

    // Catalog's seeder mints the same ids from the same ordinals, in decimal digits.
    private static Guid SeedId(string prefix, int ordinal) => Guid.Parse($"{prefix}-0000-0000-0000-{ordinal:D12}");
}
