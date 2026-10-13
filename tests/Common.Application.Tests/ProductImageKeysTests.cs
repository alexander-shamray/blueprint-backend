using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>§10.8's key shapes, which the BFF mints and the product service accepts.</summary>
public class ProductImageKeysTests
{
    private static readonly Guid Seller = new("0199b0c4-1a2b-7c3d-8e4f-5a6b7c8d9e0f");

    private static readonly Guid Image = new("0199b0c4-2b3c-7d4e-9f50-6b7c8d9e0f1a");

    [Theory]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/png", "png")]
    [InlineData("image/webp", "webp")]
    public void An_allowed_type_has_an_extension_and_the_extension_gives_the_type_back(string type, string extension)
    {
        ProductImageKeys.ExtensionFor(type).ShouldBe(extension);
        ProductImageKeys.ContentTypeFor(extension).ShouldBe(type);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("text/html")]
    [InlineData("IMAGE/PNG")]
    [InlineData("image/png; charset=utf-8")]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_outside_the_closed_list_has_an_extension(string? type)
    {
        ProductImageKeys.ExtensionFor(type).ShouldBeNull();
    }

    [Fact]
    public void A_minted_key_reads_back_for_its_seller()
    {
        string key = ProductImageKeys.UploadKey(Seller, Image, "png");

        key.ShouldBe($"uploads/{Seller}/{Image}.png");
        ProductImageKeys.TryParseUploadKey(key, Seller, out Guid image, out string extension).ShouldBeTrue();
        image.ShouldBe(Image);
        extension.ShouldBe("png");
    }

    [Fact]
    public void The_destination_key_carries_the_uploads_guid_and_extension()
    {
        // Derived, so a retried publish copies to the same place (§10.8).
        ProductImageKeys.ProductKey(Seller, Image, "webp").ShouldBe($"products/{Seller}/{Image}.webp");
    }

    [Fact]
    public void The_longest_destination_key_is_the_length_the_option_check_sizes_against()
    {
        ProductImageKeys.ProductKey(Seller, Image, "webp").Length.ShouldBe(ProductImageKeys.LongestDestinationKeyLength);
    }

    public static TheoryData<string> Refused() => new()
    {
        "",
        // Another seller's upload: the prefix check that stops attaching it.
        $"uploads/{Guid.Empty}/{Image}.png",
        // A published key is somewhere to copy to, and not from.
        $"products/{Seller}/{Image}.png",
        // A path longer than the shape, however it is spelled.
        $"uploads/{Seller}/nested/{Image}.png",
        $"uploads/{Seller}/../{Seller}/{Image}.png",
        $"uploads/{Seller}/{Image}.png/",
        $"uploads/{Seller}//{Image}.png",
        // Not a GUID, or not the canonical spelling of one.
        $"uploads/{Seller}/not-a-guid.png",
        $"uploads/{Seller}/{Image.ToString().ToUpperInvariant()}.png",
        $"uploads/{Seller}/{Image:N}.png",
        $"uploads/{Seller}/{{{Image}}}.png",
        // An extension outside the list, SVG above all, or none at all.
        $"uploads/{Seller}/{Image}.svg",
        $"uploads/{Seller}/{Image}.html",
        $"uploads/{Seller}/{Image}",
        $"uploads/{Seller}/{Image}.png.svg",
        // URLs of any scheme, which are not keys.
        "https://evil.example/desk.jpg",
        "javascript:alert(1)"
    };

    [Fact]
    public void A_missing_key_is_refused()
    {
        ProductImageKeys.TryParseUploadKey(null, Seller, out _, out _).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Only_the_exact_shape_under_the_sellers_own_prefix_is_accepted(string key)
    {
        ProductImageKeys.TryParseUploadKey(key, Seller, out Guid image, out string extension).ShouldBeFalse();

        // Nothing leaks out of a refusal for a caller to use.
        image.ShouldBe(Guid.Empty);
        extension.ShouldBeEmpty();
    }

    [Fact]
    public void The_size_ceiling_is_five_mebibytes()
    {
        ProductImageKeys.MaxSizeBytes.ShouldBe(5L * 1024 * 1024);
    }
}
