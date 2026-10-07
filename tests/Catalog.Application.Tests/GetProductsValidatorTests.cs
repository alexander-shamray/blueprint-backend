using Catalog.Application.Products.GetProducts;
using FluentValidation.Results;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>ADR-073's bounds: a closed sort, a bounded search and a cursor that keeps its query.</summary>
public class GetProductsValidatorTests
{
    private static readonly GetProductsValidator Validator = new();

    private static ValidationResult Validate(string? q = null, string? sort = null, string? cursor = null) =>
        Validator.Validate(new GetProductsQuery(cursor, 20, q, sort));

    [Fact]
    public void A_query_naming_nothing_is_valid()
    {
        Validate().IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ProductSort.Newest)]
    [InlineData(ProductSort.Name)]
    [InlineData("")]
    public void Each_sort_in_the_closed_set_is_valid_and_an_empty_one_is_the_default(string sort)
    {
        Validate(sort: sort).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("price")]
    [InlineData("Name")]
    [InlineData("name ")]
    public void A_sort_outside_the_closed_set_is_refused_on_its_field(string sort)
    {
        ValidationResult result = Validate(sort: sort);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(f => f.PropertyName == nameof(GetProductsQuery.Sort));
    }

    [Fact]
    public void A_search_at_the_ceiling_is_valid_and_one_past_it_is_refused()
    {
        // Either side of the boundary, since an off-by-one passes one of the pair and fails the other.
        Validate(q: new string('a', GetProductsValidator.MaxSearchLength)).IsValid.ShouldBeTrue();

        ValidationResult result = Validate(q: new string('a', GetProductsValidator.MaxSearchLength + 1));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(f => f.PropertyName == nameof(GetProductsQuery.Q));
    }

    [Fact]
    public void A_cursor_minted_under_the_same_query_is_valid_whatever_whitespace_surrounds_the_search()
    {
        string cursor = ProductCursor.ForName("lamp", "Desk lamp", Guid.CreateVersion7()).Encode();

        Validate(q: "  lamp ", sort: ProductSort.Name, cursor: cursor).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_cursor_minted_under_another_sort_is_refused_on_its_field()
    {
        string cursor = ProductCursor.ForNewest(null, DateTimeOffset.UtcNow, Guid.CreateVersion7()).Encode();

        ValidationResult result = Validate(sort: ProductSort.Name, cursor: cursor);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(f => f.PropertyName == nameof(GetProductsQuery.Cursor));
    }

    [Fact]
    public void A_cursor_minted_under_another_search_is_refused_on_its_field()
    {
        string cursor = ProductCursor.ForNewest("lamp", DateTimeOffset.UtcNow, Guid.CreateVersion7()).Encode();

        ValidationResult result = Validate(q: "desk", cursor: cursor);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(f => f.PropertyName == nameof(GetProductsQuery.Cursor));
    }

    [Fact]
    public void An_unreadable_cursor_is_not_refused_since_it_reads_as_the_first_page()
    {
        Validate(cursor: "not base64url!").IsValid.ShouldBeTrue();
    }
}
