namespace Catalog.Domain.Products;

/// <summary>Collection-like access to the aggregate root, one per aggregate (§5.6).</summary>
/// <remarks>Writes load through here; reads go through Dapper, never here (§6.5).</remarks>
public interface IProductRepository
{
    Task<Product?> GetAsync(ProductId id, CancellationToken ct);

    void Add(Product product);
}
