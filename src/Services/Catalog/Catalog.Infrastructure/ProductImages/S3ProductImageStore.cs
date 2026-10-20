using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Catalog.Application.Products;
using Common.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Catalog.Infrastructure.ProductImages;

/// <summary>ADR-106's port over the S3 API: reads an object's first bytes, then copies it with its own
/// metadata.</summary>
public sealed class S3ProductImageStore(
    IAmazonS3 s3,
    IOptions<ProductImageStoreOptions> options,
    ILogger<S3ProductImageStore> logger) : IProductImageStore
{
    /// <summary>The cache header every published image carries; the key is a GUID, so the bytes never change.</summary>
    public const string CacheControl = "public, max-age=31536000, immutable";

    // The longest signature is WebP's: 'RIFF', a four-byte length and 'WEBP'.
    private const int SignatureLength = 12;

    // CA1848 (ADR-019). The key holds a seller's id and an image's, which §13.4 allows.
    private static readonly Action<ILogger, string, int, Exception?> Missing =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(1, nameof(Missing)),
            "No object at {Key} (store answered {Status}); the upload is missing or expired.");

    public async Task<ImageAttachment> AttachAsync(
        string sourceKey,
        string destinationKey,
        string contentType,
        CancellationToken ct)
    {
        ProductImageStoreOptions store = options.Value;

        byte[] head = new byte[SignatureLength];
        int read;
        string storedType;

        try
        {
            // A range read, so a 5 MiB body is never pulled through this service to look at its first bytes.
            using GetObjectResponse response = await s3.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = store.Bucket,
                    Key = sourceKey,
                    ByteRange = new ByteRange(0, SignatureLength - 1)
                },
                ct);

            storedType = response.Headers.ContentType ?? "";
            read = await response.ResponseStream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            // A store answers 403 for a missing key when the caller may not list the bucket, and §10.8 gives neither
            // credential that right, so 403 is the ordinary answer for an expired upload.
            Missing(logger, sourceKey, (int)ex.StatusCode, null);
            return new ImageAttachment(ImageAttachOutcome.NotUploaded);
        }

        // Both the type the object was stored under and the bytes must agree with the key's extension, which is the
        // type the BFF signed; either one alone is something the client controls.
        if (storedType != contentType || !HasSignature(head.AsSpan(0, read), contentType))
            return new ImageAttachment(ImageAttachOutcome.NotAnImage);

        // REPLACE with nothing of the client's: the type and cache header are fixed here and its metadata is dropped.
        CopyObjectRequest copy = new()
        {
            SourceBucket = store.Bucket,
            SourceKey = sourceKey,
            DestinationBucket = store.Bucket,
            DestinationKey = destinationKey,
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = contentType
        };
        copy.Headers.CacheControl = CacheControl;

        await s3.CopyObjectAsync(copy, ct);

        return new ImageAttachment(ImageAttachOutcome.Attached, store.PublicUrlOf(destinationKey));
    }

    private static bool HasSignature(ReadOnlySpan<byte> head, string contentType) => contentType switch
    {
        "image/jpeg" => head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        "image/png" => head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/webp" => head.Length >= SignatureLength &&
            head[..4].SequenceEqual("RIFF"u8) &&
            head[8..12].SequenceEqual("WEBP"u8),
        _ => false
    };
}
