using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Common.Domain;
using Shouldly;
using Xunit;

namespace Catalog.Domain.Tests;

/// <summary>The currency guard's mixed cases, the multiplication guard's zero edge and ChangePrice's price guard.</summary>
public class CatalogBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("A1B")]
    [InlineData("AB1")]
    [InlineData("1AB")]
    public void Of_refuses_a_code_with_a_digit_among_its_letters(string currency)
    {
        // Every character must be a letter, not any one of them: a code with only some letters in it is no currency.
        Should.Throw<DomainException>(() => Money.Of(1m, currency))
            .Message.ShouldBe("Currency must be a 3-letter currency code.");
    }

    [Fact]
    public void Multiplication_by_zero_is_the_edge_the_guard_admits()
    {
        Money none = Money.Of(2.50m, "EUR") * 0;

        none.Amount.ShouldBe(0m);
        none.Currency.ShouldBe("EUR");
    }

    [Fact]
    public void ChangePrice_refuses_a_default_price_by_its_own_guard()
    {
        var product = Product.Publish("Walnut desk", null, Money.Of(19.99m, "EUR"), Now);

        // ChangePrice's currency check, next in Product, refuses default(Money) too with another message: wording proves it.
        Should.Throw<DomainException>(() => product.ChangePrice(default, Now))
            .Message.ShouldBe("A product must have a price.");
    }
}
