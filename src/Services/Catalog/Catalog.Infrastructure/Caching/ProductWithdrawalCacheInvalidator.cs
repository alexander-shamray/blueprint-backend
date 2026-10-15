using Catalog.Application.Products.GetProduct;
using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.Infrastructure.Caching;

/// <summary>§8.4's own-cache half of a withdrawal: a withdrawn product is a 404 (ADR-074), not a cached row.</summary>
/// <remarks>Public, since §6.2's scan skips internal types.</remarks>
public sealed class ProductWithdrawalCacheInvalidator(IProductReadCache cache)
    : IProjectionHandler<ProductDiscontinuedDomainEvent>
{
    public async Task HandleAsync(ProductDiscontinuedDomainEvent domainEvent, CancellationToken ct) =>
        await cache.InvalidateAsync(domainEvent.ProductId.Value, ct);
}
