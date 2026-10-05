using Common.Domain;
using Ordering.Domain.Common;
using Shouldly;
using Xunit;

namespace Ordering.Domain.Tests;

/// <summary>
/// §5.3's always-valid value object: an invalid <see cref="Money"/> cannot be
/// constructed, so nothing downstream checks for one.
/// </summary>
public class MoneyTests
{
    [Fact]
    public void Of_normalises_the_currency_and_rounds_to_two_places_banker_style()
    {
        Money money = Money.Of(10.005m, "eur");

        money.Amount.ShouldBe(10.00m, "MidpointRounding.ToEven — 10.005 rounds down");
        money.Currency.ShouldBe("EUR");
    }

    [Fact]
    public void Of_refuses_a_negative_amount()
    {
        Should.Throw<DomainException>(() => Money.Of(-0.01m, "EUR"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("1$?")]
    public void Of_refuses_anything_but_a_three_letter_code(string currency)
    {
        // "1$?" is the case length alone admits: three characters, no
        // currency — the guard checks letters, not just count.
        Should.Throw<DomainException>(() => Money.Of(1m, currency));
    }

    [Fact]
    public void Zero_is_a_valid_amount()
    {
        Money.Zero("EUR").Amount.ShouldBe(0m);
    }

    [Fact]
    public void Addition_requires_the_same_currency()
    {
        Money euros = Money.Of(1m, "EUR");
        Money dollars = Money.Of(1m, "USD");

        Should.Throw<DomainException>(() => euros + dollars);
        (euros + Money.Of(2m, "EUR")).Amount.ShouldBe(3m);
    }

    [Fact]
    public void Multiplication_scales_the_amount_and_keeps_the_currency()
    {
        Money total = Money.Of(2.50m, "EUR") * 3;

        total.Amount.ShouldBe(7.50m);
        total.Currency.ShouldBe("EUR");
    }

    [Fact]
    public void Multiplication_by_a_negative_quantity_is_the_back_door_Of_refuses()
    {
        // Without the operator's own guard this would construct the negative
        // Money the factory exists to prevent.
        Should.Throw<DomainException>(() => Money.Of(1m, "EUR") * -1);
    }

    [Fact]
    public void Two_amounts_of_the_same_currency_are_equal()
    {
        // A record struct compares by value, which is what makes Money usable
        // as a key and safe to assert on directly.
        Money.Of(1.00m, "EUR").ShouldBe(Money.Of(1m, "eur"));
    }

    [Theory]
    [InlineData(100.5, 100)]
    [InlineData(101.5, 102)]
    [InlineData(1500.49, 1500)]
    public void Of_rounds_a_currency_with_no_minor_unit_to_whole_units_banker_style(decimal amount, decimal expected)
    {
        // JPY's exponent is zero (ADR-067), so two places would keep fractions of a yen nobody can pay.
        Money.Of(amount, "jpy").Amount.ShouldBe(expected);
    }

    [Theory]
    [InlineData(1.2345, 1.234)]
    [InlineData(1.2355, 1.236)]
    [InlineData(1.234, 1.234)]
    public void Of_keeps_three_places_for_a_three_place_currency(decimal amount, decimal expected)
    {
        // KWD's exponent is three (ADR-067); two places would lose a fils from every price.
        Money.Of(amount, "KWD").Amount.ShouldBe(expected);
    }

    [Fact]
    public void Arithmetic_keeps_the_currencys_exponent()
    {
        (Money.Of(150m, "JPY") * 3 + Money.Of(50m, "JPY")).Amount.ShouldBe(500m);
        (Money.Of(1.234m, "KWD") * 3 + Money.Of(0.001m, "KWD")).Amount.ShouldBe(3.703m);
    }
}
