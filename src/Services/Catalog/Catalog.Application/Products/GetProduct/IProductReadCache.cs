using Catalog.Application.Products.GetProducts;

namespace Catalog.Application.Products.GetProduct;

/// <summary>
/// §8.2's two-tier cache for the one-product read, behind a port because Application holds no cache package (§4.2).
/// </summary>
/// <remarks>
/// A cached copy is only as fresh as its invalidators: the price and withdrawal projections and
/// <c>StockLevelProjection</c> (§8.4, E.3.3). A new column in the read's row brings its invalidator with it.
/// </remarks>
public interface IProductReadCache
{
    /// <summary>The cached row, else <paramref name="load"/>'s; a miss (<c>null</c>) is removed once stored (§8.2).</summary>
    ValueTask<ProductSummaryDto?> GetOrLoadAsync(
        Guid productId,
        Func<CancellationToken, ValueTask<ProductSummaryDto?>> load,
        CancellationToken ct);

    /// <summary>Drops the product's row from both tiers on this instance (§8.4).</summary>
    ValueTask InvalidateAsync(Guid productId, CancellationToken ct);
}
