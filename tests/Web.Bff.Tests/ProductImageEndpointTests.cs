using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Application;
using Shouldly;
using Web.Bff.ProductImages;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>E.6.11's route through the real host: who may call, what is refused, and what the server chooses.</summary>
public sealed class ProductImageEndpointTests(BffFactory factory) : IClassFixture<BffFactory>
{
    private const string Route = "/v1/product-images";

    private readonly Guid _seller = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient As(Guid subject, string? permissions)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, subject.ToString());
        if (permissions is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, permissions);

        return client;
    }

    private async Task<HttpResponseMessage> IssueAsync(object body, string? permissions = BffPermissions.CatalogWrite)
    {
        // Awaited inside the using, since a disposed client cancels the request it is still sending.
        using HttpClient client = As(_seller, permissions);

        return await client.PostAsJsonAsync(Route, body, Ct);
    }

    [Fact]
    public async Task An_anonymous_caller_is_challenged()
    {
        using HttpClient anonymous = factory.CreateClient();

        (await anonymous.PostAsJsonAsync(Route, new { ContentType = "image/png", SizeBytes = 10 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_caller_without_the_publish_permission_is_forbidden()
    {
        // Authenticated, as a buyer is: the permission is what separates a seller (§10.8, §11.4).
        (await IssueAsync(new { ContentType = "image/png", SizeBytes = 10 }, permissions: null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await IssueAsync(new { ContentType = "image/png", SizeBytes = 10 }, permissions: "orders:write"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_seller_is_given_a_URL_for_a_key_the_server_chose_under_the_sellers_own_prefix()
    {
        HttpResponseMessage response = await IssueAsync(new { ContentType = "image/webp", SizeBytes = 2048 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ProductImageUpload upload =
            (await response.Content.ReadFromJsonAsync<ProductImageUpload>(Ct)).ShouldNotBeNull();

        ProductImageKeys.TryParseUploadKey(upload.Key, _seller, out _, out string extension).ShouldBeTrue();
        extension.ShouldBe("webp");

        // The headers are the ones the signature covers, which the store refuses a request without.
        upload.Headers["Content-Type"].ShouldBe("image/webp");
        upload.Headers["Content-Length"].ShouldBe("2048");

        upload.UploadUrl.Host.ShouldBe(new Uri(BffFactory.StoreEndpoint).Host);
        upload.UploadUrl.AbsolutePath.ShouldBe($"/{BffFactory.StoreBucket}/{upload.Key}");
        upload.UploadUrl.Query.ShouldContain("X-Amz-SignedHeaders=content-length%3Bcontent-type%3Bhost");
        upload.UploadUrl.Query.ShouldContain("X-Amz-Expires=300");

        upload.ExpiresAt.ShouldBeInRange(
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(4),
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(6));
    }

    [Fact]
    public async Task The_seller_is_the_principal_s_and_the_request_cannot_name_one()
    {
        Guid other = Guid.CreateVersion7();

        HttpResponseMessage response = await IssueAsync(
            new { ContentType = "image/png", SizeBytes = 10, SellerId = other, Key = $"uploads/{other}/x.png" });

        ProductImageUpload upload =
            (await response.Content.ReadFromJsonAsync<ProductImageUpload>(Ct)).ShouldNotBeNull();
        upload.Key.ShouldStartWith($"uploads/{_seller}/");
        upload.Key.ShouldNotContain(other.ToString());
    }

    [Fact]
    public async Task Each_call_draws_its_own_key()
    {
        ProductImageUpload first = (await (await IssueAsync(new { ContentType = "image/png", SizeBytes = 10 }))
            .Content.ReadFromJsonAsync<ProductImageUpload>(Ct)).ShouldNotBeNull();
        ProductImageUpload second = (await (await IssueAsync(new { ContentType = "image/png", SizeBytes = 10 }))
            .Content.ReadFromJsonAsync<ProductImageUpload>(Ct)).ShouldNotBeNull();

        first.Key.ShouldNotBe(second.Key);
    }

    public static TheoryData<string, object> Refusals() => new()
    {
        // SVG is a document that can carry script, and is absent from the list on purpose (§10.8).
        { "ContentType", new { ContentType = "image/svg+xml", SizeBytes = 10 } },
        { "ContentType", new { ContentType = "text/html", SizeBytes = 10 } },
        // Compared exactly, since the store compares the signed value exactly.
        { "ContentType", new { ContentType = "IMAGE/PNG", SizeBytes = 10 } },
        { "ContentType", new { ContentType = "image/png; charset=utf-8", SizeBytes = 10 } },
        { "ContentType", new { ContentType = "", SizeBytes = 10 } },
        { "ContentType", new { SizeBytes = 10 } },
        { "SizeBytes", new { ContentType = "image/png", SizeBytes = ProductImageKeys.MaxSizeBytes + 1 } },
        { "SizeBytes", new { ContentType = "image/png", SizeBytes = 0 } },
        { "SizeBytes", new { ContentType = "image/png", SizeBytes = -1 } },
        // Omitted binds as null and not as zero, so it is a 400 and not a zero-byte upload.
        { "SizeBytes", new { ContentType = "image/png" } }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_type_or_size_outside_the_rules_is_a_400_naming_the_field(string field, object body)
    {
        HttpResponseMessage response = await IssueAsync(body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(field, Case.Insensitive);
    }

    [Fact]
    public async Task The_largest_allowed_size_is_accepted()
    {
        (await IssueAsync(new { ContentType = "image/jpeg", SizeBytes = ProductImageKeys.MaxSizeBytes }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void The_response_names_the_four_fields_the_contract_lists()
    {
        string[] names =
            [.. typeof(ProductImageUpload).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))];

        names.ShouldBe(["uploadUrl", "headers", "key", "expiresAt"], ignoreOrder: true);
    }
}
