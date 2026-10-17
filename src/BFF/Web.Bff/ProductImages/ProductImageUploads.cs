using System.Globalization;
using Common.Application;
using Microsoft.Extensions.Options;

namespace Web.Bff.ProductImages;

/// <summary>What the client needs to upload one image: the URL, the signed headers and the key.</summary>
public sealed record ProductImageUpload(
    Uri UploadUrl,
    IReadOnlyDictionary<string, string> Headers,
    string Key,
    DateTimeOffset ExpiresAt);

/// <summary>E.6.11's issue of a pre-signed <c>PUT</c>, and nothing else: it reads no store and writes none.</summary>
/// <remarks>A singleton, since it holds only options and a clock (§10.8).</remarks>
public sealed class ProductImageUploads(IOptions<ObjectStoreOptions> options, TimeProvider clock)
{
    /// <summary>Five minutes, E.6.11's expiry: long enough to send 5 MiB from a phone, short enough to lose.</summary>
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Mints the key and signs the <c>PUT</c> for it; the caller has checked the type and the size.</summary>
    public ProductImageUpload Issue(Guid seller, string contentType, long sizeBytes)
    {
        ObjectStoreOptions store = options.Value;

        string extension = ProductImageKeys.ExtensionFor(contentType) ??
            throw new ArgumentException($"'{contentType}' is not an allowed image type.", nameof(contentType));

        // The key is the server's alone: a GUID it draws and a seller it took from the principal (§10.8).
        string key = ProductImageKeys.UploadKey(seller, Guid.CreateVersion7(), extension);

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Content-Type"] = contentType,
            ["Content-Length"] = sizeBytes.ToString(CultureInfo.InvariantCulture)
        };

        DateTimeOffset now = clock.GetUtcNow();

        Uri url = SigV4Presigner.Presign(
            "PUT",
            ObjectUrl(store, key),
            headers,
            store.AccessKey,
            store.SecretKey,
            store.Region,
            now,
            UrlLifetime);

        return new ProductImageUpload(url, headers, key, now + UrlLifetime);
    }

    // Path style keeps the bucket in the path, which a loopback store with no wildcard DNS needs; the other puts
    // it in the host, which is how a managed store is addressed.
    private static Uri ObjectUrl(ObjectStoreOptions store, string key)
    {
        UriBuilder builder = new(store.Endpoint!);
        string basePath = builder.Path.TrimEnd('/');

        if (store.PathStyle)
        {
            builder.Path = $"{basePath}/{store.Bucket}/{key}";
        }
        else
        {
            builder.Host = $"{store.Bucket}.{builder.Host}";
            builder.Path = $"{basePath}/{key}";
        }

        return builder.Uri;
    }
}
