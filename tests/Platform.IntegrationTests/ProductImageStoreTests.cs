extern alias WebBff;

using System.Net;
using System.Net.Http.Headers;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Catalog.Application.Products;
using Catalog.Infrastructure.ProductImages;
using Common.Application;
using Common.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using WebBff::Web.Bff.ProductImages;
using Xunit;

namespace Platform.IntegrationTests;

/// <summary>
/// §10.8 against a real store: the BFF's signed URL, the product service's attach and the edge that serves the result,
/// each running the code the hosts run over the store, identities and edge §14.1 starts.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProductImageStoreTests(ObjectStoreFixture store) : IClassFixture<ObjectStoreFixture>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 2, 3];

    private static readonly byte[] Webp = [.. "RIFF"u8, 0x10, 0, 0, 0, .. "WEBP"u8, 1, 2, 3, 4];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ObjectStoreOptions BffStore()
    {
        (string accessKey, string secretKey) = ObjectStoreFixture.Credentials("bff");

        return new ObjectStoreOptions
        {
            Endpoint = store.Endpoint,
            Bucket = ObjectStoreFixture.Bucket,
            Region = "us-east-1",
            AccessKey = accessKey,
            SecretKey = secretKey,
            PathStyle = true
        };
    }

    private AmazonS3Client S3(string identity)
    {
        (string accessKey, string secretKey) = ObjectStoreFixture.Credentials(identity);

        return new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config
            {
                ServiceURL = store.Endpoint.ToString(),
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            });
    }

    private S3ProductImageStore ProductService()
    {
        (string accessKey, string secretKey) = ObjectStoreFixture.Credentials("catalog");

        return new S3ProductImageStore(
            S3("catalog"),
            Options.Create(
                new ProductImageStoreOptions
                {
                    Endpoint = store.Endpoint,
                    Bucket = ObjectStoreFixture.Bucket,
                    Region = "us-east-1",
                    AccessKey = accessKey,
                    SecretKey = secretKey,
                    PathStyle = true,
                    PublicBaseUrl = store.PublicBase
                }),
            NullLogger<S3ProductImageStore>.Instance);
    }

    private ProductImageUpload Issue(Guid seller, string contentType, long size, TimeProvider? clock = null) =>
        new ProductImageUploads(Options.Create(BffStore()), clock ?? TimeProvider.System).Issue(seller, contentType, size);

    /// <summary>The PUT a client makes with what the BFF returned, optionally corrupted by the caller.</summary>
    private static async Task<HttpResponseMessage> PutAsync(
        ProductImageUpload upload,
        byte[] body,
        string? contentType = null)
    {
        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Put, upload.UploadUrl)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? upload.Headers["Content-Type"]);

        return await client.SendAsync(request, Ct);
    }

    /// <summary>An upload the BFF signed and the client made, so the product service has something to attach.</summary>
    private async Task<(Guid Seller, ProductImageUpload Upload)> UploadedAsync(
        byte[] body,
        string contentType,
        string? storedAs = null)
    {
        Guid seller = Guid.CreateVersion7();
        ProductImageUpload upload = Issue(seller, storedAs ?? contentType, body.Length);

        (await PutAsync(upload, body)).StatusCode.ShouldBe(HttpStatusCode.OK);

        return (seller, upload);
    }

    private Task<ImageAttachment> AttachAsync(Guid seller, ProductImageUpload upload)
    {
        ProductImageKeys.TryParseUploadKey(upload.Key, seller, out Guid image, out string extension).ShouldBeTrue();

        return ProductService().AttachAsync(
            upload.Key,
            ProductImageKeys.ProductKey(seller, image, extension),
            ProductImageKeys.ContentTypeFor(extension)!,
            Ct);
    }

    [Fact]
    public async Task A_signed_URL_takes_the_object_it_was_signed_for()
    {
        ProductImageUpload upload = Issue(Guid.CreateVersion7(), "image/png", Png.Length);

        (await PutAsync(upload, Png)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_larger_body_than_the_one_signed_is_refused()
    {
        // The reason Content-Length is signed: a URL for 16 bytes cannot carry 17, let alone 5 MiB (§10.8).
        ProductImageUpload upload = Issue(Guid.CreateVersion7(), "image/png", Png.Length);

        (await PutAsync(upload, [.. Png, 9])).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_different_content_type_than_the_one_signed_is_refused()
    {
        ProductImageUpload upload = Issue(Guid.CreateVersion7(), "image/png", Png.Length);

        (await PutAsync(upload, Png, contentType: "text/html")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_expired_URL_is_refused()
    {
        ProductImageUpload upload = Issue(
            Guid.CreateVersion7(),
            "image/png",
            Png.Length,
            new PastClock(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10)));

        (await PutAsync(upload, Png)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_signature_does_not_carry_to_another_key()
    {
        ProductImageUpload upload = Issue(Guid.CreateVersion7(), "image/png", Png.Length);
        string other = upload.UploadUrl.AbsoluteUri.Replace(upload.Key, upload.Key.Replace(".png", ".jpg"), StringComparison.Ordinal);

        (await PutAsync(upload with { UploadUrl = new Uri(other) }, Png)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_BFF_s_pair_cannot_write_outside_uploads_or_read_anything()
    {
        using AmazonS3Client bff = S3("bff");
        string destination = $"products/{Guid.CreateVersion7()}/{Guid.CreateVersion7()}.png";

        (await Should.ThrowAsync<AmazonS3Exception>(() => bff.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = ObjectStoreFixture.Bucket,
                Key = destination,
                InputStream = new MemoryStream(Png)
            },
            Ct))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await Should.ThrowAsync<AmazonS3Exception>(() => bff.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = ObjectStoreFixture.Bucket },
            Ct))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_product_services_pair_cannot_write_uploads_or_list()
    {
        using AmazonS3Client catalog = S3("catalog");

        (await Should.ThrowAsync<AmazonS3Exception>(() => catalog.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = ObjectStoreFixture.Bucket,
                Key = $"uploads/{Guid.CreateVersion7()}/{Guid.CreateVersion7()}.png",
                InputStream = new MemoryStream(Png)
            },
            Ct))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await Should.ThrowAsync<AmazonS3Exception>(() => catalog.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = ObjectStoreFixture.Bucket },
            Ct))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/webp", "webp")]
    public async Task An_upload_whose_bytes_match_its_type_is_copied_and_served(string contentType, string extension)
    {
        byte[] body = contentType switch { "image/png" => Png, "image/jpeg" => Jpeg, _ => Webp };
        (Guid seller, ProductImageUpload upload) = await UploadedAsync(body, contentType);

        ImageAttachment attachment = await AttachAsync(seller, upload);

        attachment.Outcome.ShouldBe(ImageAttachOutcome.Attached);

        // Built from the base and the destination key, which carries the upload's GUID and the type's extension.
        attachment.Url.ShouldNotBeNull().ShouldStartWith($"{store.PublicBase.AbsoluteUri}products/{seller}/");
        attachment.Url.ShouldEndWith($".{extension}");

        using HttpClient client = new();
        using HttpResponseMessage served = await client.GetAsync(attachment.Url, Ct);

        served.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(body);
        served.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(contentType);

        // §10.8: a fixed type, the long cache and, from the layer in front, nosniff (§10.6).
        served.Headers.CacheControl.ShouldNotBeNull().ToString().ShouldBe(S3ProductImageStore.CacheControl);
        served.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }

    [Fact]
    public async Task The_clients_object_metadata_is_discarded_by_the_copy()
    {
        Guid seller = Guid.CreateVersion7();
        Guid image = Guid.CreateVersion7();
        string source = ProductImageKeys.UploadKey(seller, image, "png");

        // The BFF's pair, which is the only one that can write uploads/, standing in for a client's PUT.
        using (AmazonS3Client bff = S3("bff"))
        {
            PutObjectRequest put = new()
            {
                BucketName = ObjectStoreFixture.Bucket,
                Key = source,
                InputStream = new MemoryStream(Png),
                ContentType = "image/png"
            };
            put.Metadata.Add("evil", "<script>alert(1)</script>");
            await bff.PutObjectAsync(put, Ct);
        }

        string destination = ProductImageKeys.ProductKey(seller, image, "png");
        (await ProductService().AttachAsync(source, destination, "image/png", Ct)).Outcome
            .ShouldBe(ImageAttachOutcome.Attached);

        using HttpClient client = new();
        using HttpResponseMessage served = await client.GetAsync($"{store.PublicBase.AbsoluteUri}{destination}", Ct);

        served.StatusCode.ShouldBe(HttpStatusCode.OK);
        served.Headers.Select(h => h.Key).ShouldNotContain(
            key => key.StartsWith("X-Amz-Meta-", StringComparison.OrdinalIgnoreCase),
            "the client's metadata must not reach the published object");
    }

    [Fact]
    public async Task Bytes_that_are_not_the_declared_image_are_refused_and_not_copied()
    {
        // Signed and stored as a PNG, but the body is text: the type is the client's claim, the bytes are the proof.
        (Guid seller, ProductImageUpload upload) = await UploadedAsync("<html><script>1</script></html>"u8.ToArray(), "image/png");

        (await AttachAsync(seller, upload)).Outcome.ShouldBe(ImageAttachOutcome.NotAnImage);
    }

    [Fact]
    public async Task A_type_stored_other_than_the_keys_is_refused()
    {
        // A JPEG body under a key the BFF would have minted for a PNG: both halves must agree with the key.
        Guid seller = Guid.CreateVersion7();
        Guid image = Guid.CreateVersion7();
        string source = ProductImageKeys.UploadKey(seller, image, "png");

        using (AmazonS3Client bff = S3("bff"))
        {
            await bff.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = ObjectStoreFixture.Bucket,
                    Key = source,
                    InputStream = new MemoryStream(Jpeg),
                    ContentType = "image/jpeg"
                },
                Ct);
        }

        (await ProductService().AttachAsync(
                source,
                ProductImageKeys.ProductKey(seller, image, "png"),
                "image/png",
                Ct)).Outcome.ShouldBe(ImageAttachOutcome.NotAnImage);
    }

    [Fact]
    public async Task A_key_nobody_uploaded_is_not_uploaded()
    {
        Guid seller = Guid.CreateVersion7();
        Guid image = Guid.CreateVersion7();

        (await ProductService().AttachAsync(
                ProductImageKeys.UploadKey(seller, image, "png"),
                ProductImageKeys.ProductKey(seller, image, "png"),
                "image/png",
                Ct)).Outcome.ShouldBe(ImageAttachOutcome.NotUploaded);
    }

    [Fact]
    public async Task The_edge_serves_products_and_nothing_else()
    {
        (Guid seller, ProductImageUpload upload) = await UploadedAsync(Png, "image/png");
        ImageAttachment attached = await AttachAsync(seller, upload);
        using HttpClient client = new();

        // Never uploads/, though the edge is in front of the same bucket: the upload is not public.
        (await client.GetAsync($"{store.PublicBase.AbsoluteUri}{upload.Key}", Ct)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);

        (await client.GetAsync(store.PublicBase, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A read alone: the edge does not forward a write, whoever the store would let.
        using HttpResponseMessage post = await client.PostAsync(attached.Url, new ByteArrayContent(Png), Ct);
        post.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using HttpResponseMessage delete = await client.DeleteAsync(attached.Url, Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_store_does_not_serve_an_upload_to_an_anonymous_reader()
    {
        (_, ProductImageUpload upload) = await UploadedAsync(Png, "image/png");
        using HttpClient client = new();

        (await client.GetAsync(new Uri(store.Endpoint, $"{ObjectStoreFixture.Bucket}/{upload.Key}"), Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    private sealed class PastClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
