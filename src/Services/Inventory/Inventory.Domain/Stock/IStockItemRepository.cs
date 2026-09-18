namespace Inventory.Domain.Stock;

public interface IStockItemRepository
{
    /// <summary>
    /// Makes the row exist with nothing available and nothing reserved, under
    /// a lock that lets two first writes for one product both return and
    /// neither insert twice. On the unit of work's transaction.
    /// </summary>
    Task EnsureAsync(ProductId id, DateTimeOffset now, CancellationToken ct);

    Task<StockItem?> GetAsync(ProductId id, CancellationToken ct);
}
