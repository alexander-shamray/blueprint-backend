using Common.Application;

namespace Catalog.Application.Products;

/// <summary>Every <see cref="Error"/> Catalog's product slice returns, constructed here and nowhere else (§10.5).</summary>
public static class ProductErrors
{
    public static readonly Error NotFound = Error.NotFound("product.not_found", "No product with that id.");
}
