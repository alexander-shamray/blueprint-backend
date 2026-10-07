using Common.Application;

namespace Catalog.Application.Products;

/// <summary>Every <see cref="Error"/> Catalog's product slice returns, constructed here alone (§10.5).</summary>
public static class ProductErrors
{
    public static readonly Error NotFound = Error.NotFound("product.not_found", "No product with that id.");

    /// <summary>A product keeps the currency it was published in (<c>Product.ChangePrice</c>).</summary>
    public static readonly Error CurrencyFixed =
        Error.Rule("product.currency_fixed", "A product's price cannot change currency.");
}
