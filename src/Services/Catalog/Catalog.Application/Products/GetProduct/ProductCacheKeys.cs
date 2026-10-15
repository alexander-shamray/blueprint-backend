namespace Catalog.Application.Products.GetProduct;

/// <summary>The half of the key a call site writes (§8.3), and the tag §8.4 invalidates by.</summary>
public static class ProductCacheKeys
{
    public static string Key(Guid productId) => $"product:{productId}:v1";

    public static string Tag(Guid productId) => $"product:{productId}";
}
