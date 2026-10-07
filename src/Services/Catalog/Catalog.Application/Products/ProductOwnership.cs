using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.Application.Products;

/// <summary>ADR-074's resource-level check, shared by every write that loads a product (§11.4).</summary>
internal static class ProductOwnership
{
    /// <summary>
    /// True only for the seller who published it. A caller who is not is answered as for an unknown id, since a
    /// 403 would confirm the product exists; a product with no seller belongs to no caller.
    /// </summary>
    public static bool IsCallers(Product product, ICurrentUser currentUser) =>
        currentUser.IsAuthenticated && product.Seller == new SellerId(currentUser.Id);
}
