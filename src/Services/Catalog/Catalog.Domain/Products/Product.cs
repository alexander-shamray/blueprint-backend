using Catalog.Domain.Common;
using Common.Domain;

namespace Catalog.Domain.Products;

/// <summary>
/// Catalog's Product — a marketing object, which is not Inventory's SKU-and-a-
/// number and must not share a class with it (§3.1). The first slice carries
/// exactly the domain data the <c>ProductPublished</c> contract in
/// <c>Common.Contracts.Catalog.V1</c> needs — the envelope's
/// <c>MessageId</c> and <c>CorrelationId</c> are the mapper's (§9.3);
/// categories, richer media and the discontinue lifecycle arrive with the
/// PRs whose contracts need them.
/// </summary>
/// <remarks>
/// The §5.4 shape: no public setters, no parameterless public constructor, no
/// knowledge of persistence. <see cref="Publish"/> is a factory naming the
/// business operation, and the clock is a parameter — the domain never reads
/// it (§5.7).
/// </remarks>
public sealed class Product : AggregateRoot<ProductId>
{
    public string Name { get; private set; }

    public string? ThumbnailUrl { get; private set; }

    public Money Price { get; private set; }

    public DateTimeOffset PublishedAt { get; private set; }

    // EF Core materialisation only. Null-forgiving rather than a default: a
    // materialised instance is populated from columns the configuration makes
    // non-nullable, and a defaulted Name would hide a mapping hole.
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
        // Bug guards, not input validation — the validator rejects both
        // before any handler runs, so reaching a throw here means a caller
        // bypassed the always-valid boundary (§5.7). The price check exists
        // because C# hands every struct a default: Money's constructor is
        // private, but default(Money) is not, and its null Currency would
        // otherwise travel to the non-null column before failing.
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("A product must have a name.");
        if (price == default)
            throw new DomainException("A product must have a price.");

        var product = new Product(ProductId.New(), name, thumbnailUrl, price, now);

        // Staged on the Broker lane by §9.3's allow-list, in the same
        // transaction as the product. Raised whether or not anything is
        // dispatching it, because the aggregate must not teach the defect of
        // not raising: the outbox picks it up without this line changing
        // (§5.5).
        product.Raise(new ProductPublishedDomainEvent(product.Id, name, thumbnailUrl, price, now));

        return product;
    }
}
