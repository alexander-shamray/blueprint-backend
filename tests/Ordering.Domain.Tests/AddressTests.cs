using Common.Domain;
using Ordering.Domain.Common;
using Shouldly;
using Xunit;

namespace Ordering.Domain.Tests;

/// <summary>The shipping address, on §5.3's always-valid terms.</summary>
public class AddressTests
{
    [Fact]
    public void Of_trims_its_parts_and_upper_cases_the_country()
    {
        Address address = Address.Of(" 1 Test Street ", null, " Almaty ", " 050000 ", "kz");

        address.Line1.ShouldBe("1 Test Street");
        address.City.ShouldBe("Almaty");
        address.PostalCode.ShouldBe("050000");
        address.Country.ShouldBe("KZ");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_second_line_is_the_absence_of_one(string line2)
    {
        // One representation of an omitted line, so no consumer has to test for both.
        Address.Of("1 Test Street", line2, "Almaty", "050000", "KZ").Line2.ShouldBeNull();
    }

    [Fact]
    public void A_second_line_survives_when_it_is_given()
    {
        Address.Of("1 Test Street", "Flat 4", "Almaty", "050000", "KZ").Line2.ShouldBe("Flat 4");
    }

    [Theory]
    [InlineData("", "Almaty", "050000", "KZ")]
    [InlineData("   ", "Almaty", "050000", "KZ")]
    [InlineData("1 Test Street", "", "050000", "KZ")]
    [InlineData("1 Test Street", "Almaty", "", "KZ")]
    public void Of_refuses_a_missing_required_part(
        string line1,
        string city,
        string postalCode,
        string country)
    {
        Should.Throw<DomainException>(() => Address.Of(line1, null, city, postalCode, country));
    }

    [Theory]
    [InlineData("K")]
    [InlineData("KAZ")]
    [InlineData("K1")]
    [InlineData("")]
    public void Of_refuses_anything_but_a_two_letter_country_code(string country)
    {
        // KAZ is the interesting one: a caller reaching for alpha-3 rather
        // than a malformed value, which is why length alone is not the guard.
        Should.Throw<DomainException>(() =>
            Address.Of("1 Test Street", null, "Almaty", "050000", country));
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("QQ")]
    public void An_unassigned_two_letter_code_is_accepted_and_that_is_the_contract(string country)
    {
        // Shape, not membership: RegionInfo would answer from the image's ICU data, which differs between images.
        Address.Of("1 Test Street", null, "Almaty", "050000", country).Country.ShouldBe(country);
    }

    [Fact]
    public void Two_addresses_with_the_same_parts_are_equal()
    {
        Address one = Address.Of("1 Test Street", null, "Almaty", "050000", "KZ");
        Address two = Address.Of("1 Test Street", null, "Almaty", "050000", "kz");

        one.ShouldBe(two, "a value object has no identity beyond its parts");
    }
}
