using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

internal sealed class StockItemRepository(InventoryDbContext db) : IStockItemRepository
{
    // UPDLOCK, HOLDLOCK to the commit: a second first write blocks on the key-range lock and then finds the row,
    // where both would otherwise insert and the loser fail on a key ConcurrencyExceptionHandler does not map.
    public async Task EnsureAsync(ProductId id, DateTimeOffset now, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("EnsureAsync runs only inside the unit of work's transaction (§6.3).");

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt)
            SELECT {id.Value}, 0, 0, {now}
            WHERE NOT EXISTS (SELECT 1 FROM inventory.StockItems WITH (UPDLOCK, HOLDLOCK) WHERE ProductId = {id.Value});
            """,
            ct);
    }

    public Task<StockItem?> GetAsync(ProductId id, CancellationToken ct) =>
        db.StockItems.SingleOrDefaultAsync(s => s.Id == id, ct);
}
