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
        try
        {
            return await cache.GetOrCreateAsync(
                ProductCacheKeys.Key(productId),
                load,
                static async (loader, token) => await loader(token) ?? throw new MissSignal(),
                tags: [ProductCacheKeys.Tag(productId), "catalog"],
                cancellationToken: ct);
        }
        catch (MissSignal)
        {
            // A throwing factory stores nothing, so an id anyone can send leaves no entry, and no event removes one.
            return null;
        }
    }

    public ValueTask InvalidateAsync(Guid productId, CancellationToken ct) =>
        cache.RemoveByTagAsync(ProductCacheKeys.Tag(productId), ct);

    private sealed class MissSignal : Exception;
}
