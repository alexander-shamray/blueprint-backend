using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Common.Domain;
using Shouldly;
using Xunit;

namespace Catalog.Domain.Tests;

/// <summary>
/// The first aggregate, tested §12.3's way: no dependencies, no doubles, the
/// clock a fixed parameter.
/// </summary>
public class ProductTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Publish_sets_every_member_from_its_arguments()
    {
        Money price = Money.Of(19.99m, "EUR");

        var product = Product.Publish("Walnut desk", "https://cdn.example/desk.jpg", price, Now);

        product.Id.Value.ShouldNotBe(Guid.Empty);
        product.Name.ShouldBe("Walnut desk");
        product.ThumbnailUrl.ShouldBe("https://cdn.example/desk.jpg");
        product.Price.ShouldBe(price);
        product.PublishedAt.ShouldBe(Now, "the clock is a parameter — the domain never reads it (§5.4)");
    }

    [Fact]
    public void Publish_raises_the_domain_event_with_the_full_contract_payload()
    {
        // Everything ProductPublished declares rides on the event (§5.5) — a
        // field missing here is one the integration-event mapper cannot carry.
        Money price = Money.Of(19.99m, "EUR");

        var product = Product.Publish("Walnut desk", "https://cdn.example/desk.jpg", price, Now);

        IDomainEvent raised = product.DomainEvents.ShouldHaveSingleItem();
        ProductPublishedDomainEvent published = raised.ShouldBeOfType<ProductPublishedDomainEvent>();
        published.ProductId.ShouldBe(product.Id);
        published.Name.ShouldBe("Walnut desk");
        published.ThumbnailUrl.ShouldBe("https://cdn.example/desk.jpg");
        published.Price.ShouldBe(price);
        published.OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void Publish_accepts_a_product_without_a_thumbnail()
    {
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);

        product.ThumbnailUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Publish_refuses_a_blank_name(string name)
    {
        // The bug guard, not input validation — the validator rejects this
        // before any handler runs, and reaching the throw means a caller
        // bypassed the always-valid boundary (§5.7).
        Should.Throw<DomainException>(() =>
            Product.Publish(name, null, Money.Of(19.99m, "EUR"), Now));
    }

    [Fact]
    public void Publish_refuses_a_default_price()
    {
        // The language keeps default(Money) constructible however private the
        // constructor is; the aggregate is where the null currency inside it
        // must stop, not the non-null column three layers later.
        Should.Throw<DomainException>(() =>
            Product.Publish("Walnut desk", null, default, Now));
    }

    [Fact]
    public void Each_publish_mints_its_own_identity()
    {
        var first = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);
        var second = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);

        first.Id.ShouldNotBe(second.Id, "identity comes from the factory, never from the caller's data");
    }
}
