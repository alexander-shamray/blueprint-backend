using Common.Application;
using Microsoft.Extensions.Options;

namespace Catalog.Infrastructure.ProductImages;

/// <summary>The store this service attaches images from (ADR-106), bound from <c>ProductImages:Store</c>.</summary>
/// <remarks>
/// The credential pair can read <c>uploads/</c> and write <c>products/</c> and neither list nor delete (§10.8). The
/// endpoint here is the one this service reaches, which inside Compose is not the one a client reaches.
/// </remarks>
public sealed class ProductImageStoreOptions
{
    public const string SectionName = "ProductImages:Store";

    /// <summary><c>ThumbnailUrl</c>'s column width, which the stored URL must fit (§10.8).</summary>
    public const int MaxThumbnailUrlLength = 400;

    public Uri? Endpoint { get; init; }

    public string Bucket { get; init; } = "";

    public string Region { get; init; } = "";

    public string AccessKey { get; init; } = "";

    public string SecretKey { get; init; } = "";

    public bool PathStyle { get; init; }

    /// <summary>Where <c>products/</c> is served, absolute, from the store or a CDN in front of it (§10.8).</summary>
    public Uri? PublicBaseUrl { get; init; }

    /// <summary>The public URL of a key, built from the base and never echoed from a caller (§10.8).</summary>
    public string PublicUrlOf(string key) => $"{PublicBaseUrl!.AbsoluteUri.TrimEnd('/')}/{key}";
}

/// <summary>Every value present, and ADR-085's <c>https</c> rule as a rule about the configured base (§10.8).</summary>
/// <remarks>Checked by hand (§15.4), because this layer carries no data-annotations validator.</remarks>
public sealed class ProductImageStoreOptionsValidator : IValidateOptions<ProductImageStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, ProductImageStoreOptions options)
    {
        List<string> failures = [];
        const string section = ProductImageStoreOptions.SectionName;

        if (options.Endpoint is not { IsAbsoluteUri: true })
            failures.Add($"{section}:Endpoint must be an absolute URL.");
        if (string.IsNullOrWhiteSpace(options.Bucket))
            failures.Add($"{section}:Bucket is required.");
        if (string.IsNullOrWhiteSpace(options.Region))
            failures.Add($"{section}:Region is required.");
        if (string.IsNullOrWhiteSpace(options.AccessKey))
            failures.Add($"{section}:AccessKey is required.");
        if (string.IsNullOrWhiteSpace(options.SecretKey))
            failures.Add($"{section}:SecretKey is required.");

        Uri? baseUrl = options.PublicBaseUrl;

        if (baseUrl is not { IsAbsoluteUri: true })
        {
            failures.Add($"{section}:PublicBaseUrl must be an absolute URL.");
        }
        else
        {
            // Loopback is the exception: Compose's local store is plain http on 127.0.0.1 (§14.1), and mixed content
            // on a buyer's page is a risk a deployed host has and a developer's machine does not.
            if (baseUrl.Scheme != Uri.UriSchemeHttps && !(baseUrl.Scheme == Uri.UriSchemeHttp && baseUrl.IsLoopback))
                failures.Add($"{section}:PublicBaseUrl must be an https URL.");

            if (baseUrl.Query.Length > 0 || baseUrl.Fragment.Length > 0 || !string.IsNullOrEmpty(baseUrl.UserInfo))
                failures.Add($"{section}:PublicBaseUrl must carry no query, fragment or user.");

            int longest = options.PublicUrlOf(new string('x', ProductImageKeys.LongestDestinationKeyLength)).Length;
            if (longest > ProductImageStoreOptions.MaxThumbnailUrlLength)
            {
                failures.Add(
                    $"{section}:PublicBaseUrl is too long: it and the longest destination key must fit " +
                    $"{ProductImageStoreOptions.MaxThumbnailUrlLength} characters.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
