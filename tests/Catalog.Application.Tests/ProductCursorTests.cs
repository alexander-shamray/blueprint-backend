using Catalog.Application.Products.GetProducts;
using Common.Application;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>ADR-073's cursor: it carries the ordering and the search it was minted under, beside the seek.</summary>
public class ProductCursorTests
{
    [Theory]
    [InlineData(null, "Desk lamp")]
    [InlineData("lamp", "Desk: walnut")]
    [InlineData("12:3", "4:56")]
    public void A_cursor_round_trips_its_ordering_search_key_and_id(string? search, string name)
    {
        // The search and the name may each hold the payload's own separator, and a digit after the search's.
        ProductCursor minted = ProductCursor.ForName(search, name, Guid.CreateVersion7());

        ProductCursor.Decode(minted.Encode()).ShouldBe(minted);
    }

    [Fact]
    public void A_cursor_matches_the_ordering_and_search_it_was_minted_under_and_no_other()
    {
        ProductCursor minted = ProductCursor.ForName("lamp", "Desk lamp", Guid.CreateVersion7());

        minted.Matches(ProductSort.Name, "lamp").ShouldBeTrue();
        minted.Matches(ProductSort.Newest, "lamp").ShouldBeFalse();
        minted.Matches(ProductSort.Name, "lamps").ShouldBeFalse();
        minted.Matches(ProductSort.Name, null).ShouldBeFalse();
    }

    [Fact]
    public void A_newest_cursor_keeps_its_instant_in_utc()
    {
        DateTimeOffset local = new(2026, 8, 1, 14, 0, 0, TimeSpan.FromHours(2));

        ProductCursor? decoded = ProductCursor.Decode(ProductCursor.ForNewest(null, local, Guid.Empty).Encode());

        decoded.ShouldNotBeNull();
        decoded.PublishedAt.ShouldBe(local);
        decoded.PublishedAt!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not base64url!")]
    public void Anything_unreadable_decodes_to_null(string? cursor)
    {
        ProductCursor.Decode(cursor).ShouldBeNull();
    }

    [Fact]
    public void A_cursor_from_before_the_listing_took_a_sort_decodes_to_null()
    {
        // The old shape gets the first page, as any unreadable cursor does (§6.5).
        string legacy = Cursor.Encode(DateTimeOffset.UtcNow, Guid.CreateVersion7());

        ProductCursor.Decode(legacy).ShouldBeNull();
    }

    [Theory]
    [InlineData("price:0199b0c46f2e7a318c5d2e4f6a7b8c9d:0:1")]
    [InlineData("newest:0199b0c46f2e7a318c5d2e4f6a7b8c9d:0:yesterday")]
    [InlineData("newest:0199b0c46f2e7a318c5d2e4f6a7b8c9d:0:99999999999999999999")]
    [InlineData("newest:not-a-guid:0:1")]
    [InlineData("name:0199b0c46f2e7a318c5d2e4f6a7b8c9d:9:lamp")]
    [InlineData("name:0199b0c46f2e7a318c5d2e4f6a7b8c9d:-1:lamp")]
    [InlineData("name:0199b0c46f2e7a318c5d2e4f6a7b8c9d:4:lamp")]
    [InlineData("name:")]
    public void A_payload_with_an_unknown_sort_or_an_unreadable_field_decodes_to_null(string payload)
    {
        ProductCursor.Decode(Cursor.Wrap(payload)).ShouldBeNull();
    }
}
