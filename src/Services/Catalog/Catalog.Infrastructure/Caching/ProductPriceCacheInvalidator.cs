using Catalog.Application.Products.GetProduct;
using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.Infrastructure.Caching;

/// <summary>§8.4's own-cache half of a price change, on the <c>Local</c> lane so a crash after commit retries it.</summary>
/// <remarks>Public, since §6.2's scan skips internal types.</remarks>
public sealed class ProductPriceCacheInvalidator(IProductReadCache cache)
    : IProjectionHandler<PriceChangedDomainEvent>
{
    public async Task HandleAsync(PriceChangedDomainEvent domainEvent, CancellationToken ct) =>
        await cache.InvalidateAsync(domainEvent.ProductId.Value, ct);
}
