namespace Inventory.Domain.Stock;

public interface IStockItemRepository
{
    /// <summary>Makes the row exist, empty, under a lock so two first writes for one product insert once.</summary>
    Task EnsureAsync(ProductId id, DateTimeOffset now, CancellationToken ct);

    Task<StockItem?> GetAsync(ProductId id, CancellationToken ct);
}
