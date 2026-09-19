using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

internal sealed class StockItemRepository(InventoryDbContext db) : IStockItemRepository
{
    // UPDLOCK, HOLDLOCK on the probe, held to the commit: two first writes
    // for one product both reach this statement, the second blocks on the
    // first's key-range lock and finds the row when it proceeds — without it
    // both insert and the loser fails on the key, which
    // ConcurrencyExceptionHandler does not map. On a row that exists the same
    // lock serialises this write with the ledger's statements on the row.
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
