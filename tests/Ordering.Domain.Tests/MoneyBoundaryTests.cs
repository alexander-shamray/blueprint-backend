using Common.Domain;
using Ordering.Domain.Common;
using Shouldly;
using Xunit;

namespace Ordering.Domain.Tests;

/// <summary>The currency guard's mixed cases and the multiplication guard's zero edge.</summary>
public class MoneyBoundaryTests
{
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
}
