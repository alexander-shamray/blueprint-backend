using Catalog.Application.Products.GetProducts;

namespace Catalog.Application.Products.GetProduct;

/// <summary>
/// §8.2's two-tier cache for the one-product read, behind a port because Application holds no cache package (§4.2).
/// </summary>
/// <remarks>As fresh as its invalidators, which E.3.3 lists (§8.4).</remarks>
public interface IProductReadCache
{
    /// <summary>The cached row, else <paramref name="load"/>'s; a miss (<c>null</c>) is never stored (§8.2).</summary>
    ValueTask<ProductSummaryDto?> GetOrLoadAsync(
        Guid productId,
        Func<CancellationToken, ValueTask<ProductSummaryDto?>> load,
        CancellationToken ct);

    /// <summary>Drops the product's row from both tiers on this instance (§8.4).</summary>
    ValueTask InvalidateAsync(Guid productId, CancellationToken ct);
}
