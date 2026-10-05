using Common.Domain;
using Shouldly;
using Xunit;

namespace Common.Domain.Tests;

/// <summary>ADR-067's table: ISO 4217's exponent per code, read by every context's money.</summary>
public class CurrencyMinorUnitsTests
{
    [Theory]
    [InlineData("JPY", 0)]
    [InlineData("KRW", 0)]
    [InlineData("XOF", 0)]
    [InlineData("KWD", 3)]
    [InlineData("BHD", 3)]
    [InlineData("TND", 3)]
    [InlineData("CLF", 4)]
    [InlineData("GBP", 2)]
    [InlineData("KZT", 2)]
    [InlineData("EUR", 2)]
    public void A_code_answers_its_iso_4217_exponent(string currency, int exponent)
    {
        CurrencyMinorUnits.Of(currency).ShouldBe(exponent);
    }

    [Fact]
    public void The_lookup_ignores_case_since_money_normalises_after_it()
    {
        CurrencyMinorUnits.Of("jpy").ShouldBe(0);
        CurrencyMinorUnits.Of("kwd").ShouldBe(3);
    }

    [Fact]
    public void A_code_the_table_does_not_list_is_the_default()
    {
        // ZZZ is no ISO code: a deployment's own test currency, which Money already admits by shape alone.
        CurrencyMinorUnits.Of("ZZZ").ShouldBe(CurrencyMinorUnits.Default);
    }
}
