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

    [Fact]
    public void ChangePrice_reprices_and_raises_the_contract_payload()
    {
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);
        product.ClearDomainEvents();
        DateTimeOffset later = Now.AddHours(1);

        product.ChangePrice(Money.Of(24.50m, "EUR"), later);

        product.Price.ShouldBe(Money.Of(24.50m, "EUR"));
        PriceChangedDomainEvent changed = product.DomainEvents
            .ShouldHaveSingleItem()
            .ShouldBeOfType<PriceChangedDomainEvent>();
        changed.ProductId.ShouldBe(product.Id);
        changed.Price.ShouldBe(Money.Of(24.50m, "EUR"));
        changed.OccurredAt.ShouldBe(later);
    }

    [Fact]
    public void ChangePrice_to_the_same_price_raises_nothing()
    {
        // A change to the price the product already has is no change, so nothing is published for it.
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);
        product.ClearDomainEvents();

        product.ChangePrice(Money.Of(19.99m, "eur"), Now.AddHours(1));

        product.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void ChangePrice_refuses_another_currency()
    {
        // The handler refuses it first as a rule failure; reaching the guard is a bug (§5.7).
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);

        Should.Throw<DomainException>(() => product.ChangePrice(Money.Of(19.99m, "USD"), Now));
        product.Price.ShouldBe(Money.Of(19.99m, "EUR"));
    }

    [Fact]
    public void ChangePrice_refuses_a_default_price()
    {
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);

        Should.Throw<DomainException>(() => product.ChangePrice(default, Now));
    }

    [Fact]
    public void Publish_records_the_seller_it_is_given()
    {
        var seller = new SellerId(Guid.CreateVersion7());

        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now, seller);

        product.Seller.ShouldBe(seller);
        product.WithdrawnAt.ShouldBeNull();
    }

    [Fact]
    public void Withdraw_stamps_the_product_and_raises_the_contract_payload()
    {
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);
        product.ClearDomainEvents();
        DateTimeOffset later = Now.AddHours(1);

        product.Withdraw(later);

        product.WithdrawnAt.ShouldBe(later);
        ProductDiscontinuedDomainEvent discontinued = product.DomainEvents
            .ShouldHaveSingleItem()
            .ShouldBeOfType<ProductDiscontinuedDomainEvent>();
        discontinued.ProductId.ShouldBe(product.Id);
        discontinued.OccurredAt.ShouldBe(later);
    }

    [Fact]
    public void Withdraw_refuses_a_product_already_withdrawn()
    {
        // The handler refuses it first as a rule failure; reaching the guard is a bug (§5.7).
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);
        product.Withdraw(Now.AddHours(1));
        product.ClearDomainEvents();

        Should.Throw<DomainException>(() => product.Withdraw(Now.AddHours(2)));
        product.WithdrawnAt.ShouldBe(Now.AddHours(1));
        product.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void ChangePrice_refuses_a_withdrawn_product()
    {
        // A later PriceChanged would re-list the product in Ordering's projection (§6.6), so a withdrawal is final.
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);
        product.Withdraw(Now.AddHours(1));
        product.ClearDomainEvents();

        Should.Throw<DomainException>(() => product.ChangePrice(Money.Of(24.50m, "EUR"), Now.AddHours(2)));
        product.Price.ShouldBe(Money.Of(19.99m, "EUR"));
        product.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Withdraw_on_a_clock_behind_the_last_price_stamps_after_that_price()
    {
        // #607: the price committed first on a replica whose clock runs ahead; Ordering's projection keeps a
        // price row the withdrawal's stamp does not cover, so the stamp must follow the commits (ADR-075).
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);
        DateTimeOffset aheadClock = Now.AddMilliseconds(500);
        product.ChangePrice(Money.Of(24.50m, "EUR"), aheadClock);
        product.ClearDomainEvents();

        product.Withdraw(Now.AddMilliseconds(400));

        ProductDiscontinuedDomainEvent discontinued = product.DomainEvents
            .ShouldHaveSingleItem()
            .ShouldBeOfType<ProductDiscontinuedDomainEvent>();
        discontinued.OccurredAt.ShouldBe(aheadClock.AddTicks(1));
        product.WithdrawnAt.ShouldBe(discontinued.OccurredAt);
        product.LastEventAt.ShouldBe(discontinued.OccurredAt);
    }

    [Fact]
    public void ChangePrice_on_a_clock_behind_the_last_price_stamps_after_that_price()
    {
        // Two prices at one stamp, or in the stamps' wrong order, leave delivery order to decide the amount (§6.6).
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);
        product.ChangePrice(Money.Of(24.50m, "EUR"), Now);
        product.ClearDomainEvents();

        product.ChangePrice(Money.Of(29.00m, "EUR"), Now.AddMilliseconds(-200));

        PriceChangedDomainEvent changed = product.DomainEvents
            .ShouldHaveSingleItem()
            .ShouldBeOfType<PriceChangedDomainEvent>();
        changed.OccurredAt.ShouldBe(Now.AddTicks(2));
        product.LastEventAt.ShouldBe(changed.OccurredAt);
    }

    [Fact]
    public void Publish_starts_the_products_stamps_at_its_publication()
    {
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);

        product.LastEventAt.ShouldBe(Now);
    }
}
