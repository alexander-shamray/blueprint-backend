using Catalog.Infrastructure.ProductImages;
using Common.Application;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>ADR-085's <c>https</c> rule as a rule about the configured base, checked when the host starts
/// (§10.8).</summary>
public class ProductImageStoreOptionsTests
{
    private static readonly ProductImageStoreOptionsValidator Validator = new();

    private static ProductImageStoreOptions Valid(string baseUrl = "https://images.example.com") =>
        new()
        {
            Endpoint = new Uri("https://objects.example.com"),
            Bucket = "product-images",
            Region = "us-east-1",
            AccessKey = "not-a-real-key",
            SecretKey = "not-a-real-secret",
            PublicBaseUrl = new Uri(baseUrl)
        };

    [Theory]
    [InlineData("https://images.example.com")]
    [InlineData("https://images.example.com/")]
    [InlineData("https://cdn.example.com/catalogue")]
    // Compose's local edge, and the only plain http a base may be (§14.1).
    [InlineData("http://127.0.0.1:9000")]
    [InlineData("http://localhost:9000")]
    public void A_base_the_rule_admits_passes(string baseUrl)
    {
        Validator.Validate(null, Valid(baseUrl)).Succeeded.ShouldBeTrue(baseUrl);
    }

    [Theory]
    [InlineData("http://images.example.com")]
    [InlineData("ftp://images.example.com")]
    [InlineData("https://user:secret@images.example.com")]
    [InlineData("https://images.example.com/?v=1")]
    [InlineData("https://images.example.com/#top")]
    public void A_base_the_rule_refuses_fails_naming_the_key(string baseUrl)
    {
        ValidateOptionsResult result = Validator.Validate(null, Valid(baseUrl));

        result.Failed.ShouldBeTrue(baseUrl);
        result.FailureMessage.ShouldContain("PublicBaseUrl");
    }

    [Fact]
    public void A_base_so_long_that_the_longest_key_overflows_the_column_fails()
    {
        // The stored URL is the base and a key, and ThumbnailUrl is 400 wide (§10.8).
        string longBase = "https://images.example.com/" + new string('x', 320);

        Validator.Validate(null, Valid(longBase)).Failed.ShouldBeTrue();
    }

    [Fact]
    public void A_base_that_exactly_fits_the_column_passes()
    {
        string baseUrl = "https://images.example.com/";
        int room = ProductImageStoreOptions.MaxThumbnailUrlLength
            - ProductImageKeys.LongestDestinationKeyLength
            - 1
            - baseUrl.Length;

        ProductImageStoreOptions options = Valid(baseUrl + new string('x', room));

        options.PublicUrlOf(new string('k', ProductImageKeys.LongestDestinationKeyLength)).Length
            .ShouldBe(ProductImageStoreOptions.MaxThumbnailUrlLength);
        Validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_missing_value_fails_and_names_it()
    {
        ValidateOptionsResult result = Validator.Validate(null, new ProductImageStoreOptions());

        result.Failed.ShouldBeTrue();
        foreach (string key in new[] { "Endpoint", "Bucket", "Region", "AccessKey", "SecretKey", "PublicBaseUrl" })
            result.FailureMessage.ShouldContain(key);
    }

    [Fact]
    public void The_public_url_is_built_from_the_base_and_the_key_and_nothing_else()
    {
        Valid("https://images.example.com/").PublicUrlOf("products/a/b.png")
            .ShouldBe("https://images.example.com/products/a/b.png");
        Valid("https://images.example.com").PublicUrlOf("products/a/b.png")
            .ShouldBe("https://images.example.com/products/a/b.png");
    }
}
