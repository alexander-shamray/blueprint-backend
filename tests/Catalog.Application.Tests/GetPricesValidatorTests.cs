using Catalog.Application.Products.GetPrices;
using FluentValidation.Results;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>The input half of §9.7's hop, validated as a command is (§6.3), so the id list is bounded.</summary>
public class GetPricesValidatorTests
{
    private static readonly GetPricesValidator Validator = new();

    private static ValidationResult Validate(int productCount, string currency) =>
        Validator.Validate(
            new GetPricesQuery([.. Enumerable.Range(0, productCount).Select(_ => Guid.CreateVersion7())], currency));

    [Fact]
    public void An_empty_id_list_is_valid()
    {
        // A legal request with an empty answer, which the handler returns without touching the database.
        Validate(0, "GBP").IsValid.ShouldBeTrue();
    }

    [Fact]
    public void The_ceiling_is_inclusive()
    {
        Validate(GetPricesValidator.MaxProductIds, "GBP").IsValid.ShouldBeTrue();
    }

    [Fact]
    public void One_past_the_ceiling_is_refused()
    {
        // Either side of the boundary, since an off-by-one passes one of the pair and fails the other.
        ValidationResult result = Validate(GetPricesValidator.MaxProductIds + 1, "GBP");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(f => f.PropertyName.Contains("ProductIds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("GB")]
    [InlineData("GBPX")]
    [InlineData("12A")]
    [InlineData("GBP\n")]
    public void A_currency_that_is_not_three_letters_is_refused(string currency)
    {
        // PublishProductValidator's rule, ending \z rather than $, which matches before a trailing newline.
        Validate(1, currency).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_three_letter_currency_is_accepted()
    {
        Validate(1, "gbp").IsValid.ShouldBeTrue();
    }
}
