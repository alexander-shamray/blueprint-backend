using Catalog.Domain.Common;
using Common.Domain;

namespace Catalog.Domain.Products;

/// <summary>Catalog's Product, a marketing object that must not share a class with Inventory's SKU (§3.1).</summary>
/// <remarks>The §5.4 shape; the clock is a parameter, because the domain never reads it (§5.7).</remarks>
public sealed class Product : AggregateRoot<ProductId>
{
    public string Name { get; private set; }

    public string? ThumbnailUrl { get; private set; }

    public Money Price { get; private set; }

    public DateTimeOffset PublishedAt { get; private set; }

    // EF Core materialisation only; null-forgiving, so a defaulted Name cannot hide a mapping hole.
    private Product() => Name = null!;

    private Product(ProductId id, string name, string? thumbnailUrl, Money price, DateTimeOffset publishedAt)
    {
        Id = id;
        Name = name;
        ThumbnailUrl = thumbnailUrl;
        Price = price;
        PublishedAt = publishedAt;
    }

    public static Product Publish(string name, string? thumbnailUrl, Money price, DateTimeOffset now)
    {
        // Bug guards, not input validation: the validator refuses both first (§5.7). The price check catches
        // default(Money), which Money's private constructor cannot prevent.
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("A product must have a name.");
        if (price == default)
            throw new DomainException("A product must have a price.");

        var product = new Product(ProductId.New(), name, thumbnailUrl, price, now);

        // Raised whether or not anything dispatches it (§5.5); §9.3's allow-list stages it on the Broker lane.
        product.Raise(new ProductPublishedDomainEvent(product.Id, name, thumbnailUrl, price, now));

        return product;
    }
}
