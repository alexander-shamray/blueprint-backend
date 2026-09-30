namespace Catalog.Domain.Products;

/// <summary>Collection-like access to the aggregate root, one per aggregate (§5.6).</summary>
/// <remarks><c>Add</c> only until a command loads a product; reads go through Dapper, never here (§6.5).</remarks>
public interface IProductRepository
{
    void Add(Product product);
}
