using Catalog.Application.Products.GetProduct;
using Catalog.Application.Products.GetProducts;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalog.Infrastructure.Caching;

/// <summary>§8.2's sample over <see cref="HybridCache"/>, tagged for §8.4's invalidators.</summary>
internal sealed class HybridProductReadCache(HybridCache cache) : IProductReadCache
{
    public async ValueTask<ProductSummaryDto?> GetOrLoadAsync(
        Guid productId,
        Func<CancellationToken, ValueTask<ProductSummaryDto?>> load,
        CancellationToken ct)
    {
        string key = ProductCacheKeys.Key(productId);

        ProductSummaryDto? product = await cache.GetOrCreateAsync(
            key,
            load,
            static (loader, token) => loader(token),
            tags: [ProductCacheKeys.Tag(productId), "catalog"],
            cancellationToken: ct);

        // An id anyone can send must not fill the cache with misses, and no event removes an unknown id's.
        if (product is null)
            await cache.RemoveAsync(key, ct);

        return product;
    }

    public ValueTask InvalidateAsync(Guid productId, CancellationToken ct) =>
        cache.RemoveByTagAsync(ProductCacheKeys.Tag(productId), ct);
}
