using Catalog.Domain.Products;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Infrastructure.Persistence;

/// <summary>§5.6's implementation half, matching the port.</summary>
internal sealed class ProductRepository(CatalogDbContext db) : IProductRepository
{
    public Task<Product?> GetAsync(ProductId id, CancellationToken ct) =>
        db.Set<Product>().SingleOrDefaultAsync(p => p.Id == id, ct);

    public void Add(Product product) => db.Add(product);
}
