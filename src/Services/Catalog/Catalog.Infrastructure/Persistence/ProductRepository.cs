using Catalog.Domain.Products;

namespace Catalog.Infrastructure.Persistence;

/// <summary>§5.6's implementation half, <c>Add</c> only to match the port.</summary>
internal sealed class ProductRepository(CatalogDbContext db) : IProductRepository
{
    public void Add(Product product) => db.Add(product);
}
