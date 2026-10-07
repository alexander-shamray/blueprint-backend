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

    /// <summary>Null for a product no person published: the seeder's, and every row older than the column.</summary>
    /// <remarks>No seller owns such a product, so ADR-074's ownership check refuses every caller on it.</remarks>
    public SellerId? Seller { get; private set; }

    /// <summary>Set once by <see cref="Withdraw"/> and never cleared: a withdrawal is final (ADR-074).</summary>
    public DateTimeOffset? WithdrawnAt { get; private set; }

    // EF Core materialisation only; null-forgiving, so a defaulted Name cannot hide a mapping hole.
    private Product() => Name = null!;

    private Product(
        ProductId id,
        string name,
        string? thumbnailUrl,
        Money price,
        DateTimeOffset publishedAt,
        SellerId? seller)
    {
        Id = id;
        Name = name;
        ThumbnailUrl = thumbnailUrl;
        Price = price;
        PublishedAt = publishedAt;
        Seller = seller;
    }

    public static Product Publish(
        string name,
        string? thumbnailUrl,
        Money price,
        DateTimeOffset now,
        SellerId? seller = null)
    {
        // Bug guards, not input validation: the validator refuses both first (§5.7). The price check catches
        // default(Money), which Money's private constructor cannot prevent.
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("A product must have a name.");
        if (price == default)
            throw new DomainException("A product must have a price.");

        var product = new Product(ProductId.New(), name, thumbnailUrl, price, now, seller);

        // Raised whether or not anything dispatches it (§5.5); §9.3's allow-list stages it on the Broker lane.
        product.Raise(new ProductPublishedDomainEvent(product.Id, name, thumbnailUrl, price, now));

        return product;
    }

    /// <summary>Reprices the product in its own currency; the same price again raises nothing (§5.4).</summary>
    /// <remarks>
    /// The currency is fixed at publication, because Ordering's projection keys a price by currency and a change
    /// of currency would leave the old row orderable at the old amount (§6.6).
    /// </remarks>
    public void ChangePrice(Money price, DateTimeOffset now)
    {
        // Bug guards: the validator and the handler refuse all three first (§5.7).
        if (price == default)
            throw new DomainException("A product must have a price.");
        if (price.Currency != Price.Currency)
            throw new DomainException("A product's price cannot change currency.");
        // A PriceChanged after the withdrawal would re-list the product in Ordering's projection (§6.6).
        if (WithdrawnAt is not null)
            throw new DomainException("A withdrawn product's price cannot change.");

        if (price == Price)
            return;

        Price = price;
        Raise(new PriceChangedDomainEvent(Id, price, now));
    }

    /// <summary>Takes the product off sale for good; the listing hides it and new orders cannot price it.</summary>
    public void Withdraw(DateTimeOffset now)
    {
        // Bug guard: the handler refuses a second withdrawal first, as a rule failure (§5.7).
        if (WithdrawnAt is not null)
            throw new DomainException("The product is already withdrawn.");

        WithdrawnAt = now;
        Raise(new ProductDiscontinuedDomainEvent(Id, now));
    }
}
