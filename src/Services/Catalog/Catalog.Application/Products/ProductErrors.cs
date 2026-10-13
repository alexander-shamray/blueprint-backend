using Common.Application;

namespace Catalog.Application.Products;

/// <summary>Every <see cref="Error"/> Catalog's product slice returns, constructed here alone (§10.5).</summary>
public static class ProductErrors
{
    public static readonly Error NotFound = Error.NotFound("product.not_found", "No product with that id.");

    /// <summary>A product keeps the currency it was published in (<c>Product.ChangePrice</c>).</summary>
    public static readonly Error CurrencyFixed =
        Error.Rule("product.currency_fixed", "A product's price cannot change currency.");

    /// <summary>A withdrawal is final, so neither a second one nor a price change follows it (ADR-074).</summary>
    public static readonly Error Withdrawn = Error.Rule("product.withdrawn", "The product has been withdrawn.");

    /// <summary>Not the shape the BFF mints, or under another seller's prefix (§10.8).</summary>
    public static readonly Error ImageKeyInvalid =
        Error.Rule("product.image_key_invalid", "The image key is not one this seller's upload returned.");

    /// <summary>Nothing at the key: the upload was never made, was refused, or has expired (§10.8).</summary>
    public static readonly Error ImageNotUploaded =
        Error.Rule("product.image_not_uploaded", "No image has been uploaded under that key.");

    /// <summary>The bytes are not the image their declared type says they are (§10.8).</summary>
    public static readonly Error ImageNotAnImage =
        Error.Rule("product.image_not_an_image", "The uploaded file is not the image its content type declares.");
}
