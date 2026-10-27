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

    [Theory]
    [InlineData("BIF")]
    [InlineData("CLP")]
    [InlineData("DJF")]
    [InlineData("GNF")]
    [InlineData("ISK")]
    [InlineData("JPY")]
    [InlineData("KMF")]
    [InlineData("KRW")]
    [InlineData("PYG")]
    [InlineData("RWF")]
    [InlineData("UGX")]
    [InlineData("UYI")]
    [InlineData("VND")]
    [InlineData("VUV")]
    [InlineData("XAF")]
    [InlineData("XOF")]
    [InlineData("XPF")]
    public void Every_listed_zero_exponent_code_answers_zero(string currency)
    {
        CurrencyMinorUnits.Of(currency).ShouldBe(0);
    }

    [Theory]
    [InlineData("BHD")]
    [InlineData("IQD")]
    [InlineData("JOD")]
    [InlineData("KWD")]
    [InlineData("LYD")]
    [InlineData("OMR")]
    [InlineData("TND")]
    public void Every_listed_three_exponent_code_answers_three(string currency)
    {
        CurrencyMinorUnits.Of(currency).ShouldBe(3);
    }

    [Theory]
    [InlineData("CLF")]
    [InlineData("UYW")]
    public void Every_listed_four_exponent_code_answers_four(string currency)
    {
        CurrencyMinorUnits.Of(currency).ShouldBe(4);
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
