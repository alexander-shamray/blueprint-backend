using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>§10.1's compression over the proxy (ADR-020), read by a client that does not decompress.</summary>
public sealed class CompressedResponseTests(StubDestination stub) : IClassFixture<StubDestination>
{
    /// <summary>Large enough that compression cannot fail to shrink it.</summary>
    private const int BodyBytes = 8192;

    private static readonly string CompressibleRoute =
        $"/api/v1/catalog/products?{StubDestination.BodySizeQuery}={BodyBytes}";

    [Fact]
    public async Task A_proxied_json_response_is_compressed_and_round_trips()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, CompressibleRoute);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBe(["gzip"]);

        // Both, or a shared cache may serve the encoded body to a client that never asked for it (ADR-020).
        response.Headers.Vary.ShouldContain("Accept-Encoding");
        response.Headers.Vary.ShouldContain("Cache-Control");

        byte[] encoded = await response.Content.ReadAsByteArrayAsync(ct);

        encoded.Length.ShouldBeLessThan(
            BodyBytes,
            "the response is encoded, so the bytes on the wire are the compressed ones");

        // The whole body: a truncating pipeline would satisfy every assertion above.
        Decompress(encoded).ShouldBe(new string('a', BodyBytes));
    }

    /// <summary>ADR-020's <c>EnableForHttps</c>, the one setting whose default answers the other way.</summary>
    [Fact]
    public async Task An_https_response_is_compressed_too()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using StubbedGatewayFactory factory = new(stub.Address);
        factory.ClientOptions.BaseAddress = new Uri("https://localhost");

        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, CompressibleRoute);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        HttpResponseMessage response = await client.SendAsync(request, ct);

        response.RequestMessage!.RequestUri!.Scheme.ShouldBe(
            Uri.UriSchemeHttps,
            "otherwise this is the previous test again under another name");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBe(["gzip"]);
    }

    /// <summary>§10.5's error shape is left out of the default MIME list, pinned from the wire (ADR-020).</summary>
    /// <remarks>A 401, because the ordering route's authenticated policy (§11.4) refuses an anonymous GET.</remarks>
    [Fact]
    public async Task A_problem_json_error_is_not_compressed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/orders/018f4c2e");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        response.Content.Headers.ContentEncoding.ShouldBeEmpty();

        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("\"status\":401");
    }

    [Fact]
    public async Task An_already_encoded_response_is_passed_through_once()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await Get(Declaring("gzip"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBe(
            ["gzip"],
            "one encoding — a second would be the destination's gzip inside the gateway's");

        Decompress(await response.Content.ReadAsByteArrayAsync(ct)).ShouldBe(new string('a', BodyBytes));
    }

    /// <summary>
    /// The value-blind guard; ADR-020's opt-out is <see cref="A_no_transform_directive_stops_compression"/>.
    /// </summary>
    [Fact]
    public async Task The_existing_encoding_guard_skips_any_declared_value()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await Get(Declaring("identity"), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBe(
            ["identity"],
            "the declaration survives the hop untouched — a gzip here would mean the guard reads the value");

        (await response.Content.ReadAsStringAsync(ct)).ShouldBe(new string('a', BodyBytes));
    }

    /// <summary>ADR-020's opt-out, which the gateway's own provider enforces and the framework does not.</summary>
    [Fact]
    public async Task A_no_transform_directive_stops_compression()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await Get(
            $"{CompressibleRoute}&{StubDestination.NoTransformQuery}=1",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoTransform.ShouldBe(
            true,
            "the directive survived the hop, so this is a statement about the middleware and not about YARP");

        response.Content.Headers.ContentEncoding.ShouldBeEmpty(
            "RFC 9111 forbids an intermediary transforming this content, and the gateway is one");

        (await response.Content.ReadAsStringAsync(ct)).ShouldBe(new string('a', BodyBytes));
    }

    /// <summary>The request directive is honoured too, though RFC 9111 makes it an ask (ADR-020).</summary>
    [Fact]
    public async Task A_client_asking_for_no_transformation_is_not_sent_a_compressed_body()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, CompressibleRoute);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoTransform = true };

        HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBeEmpty(
            "the caller asked not to have its content transformed, and the destination said nothing either way");

        response.Headers.Vary.ShouldContain(
            "Cache-Control",
            "the representation depends on the request header, so it is a cache-selection dimension");

        (await response.Content.ReadAsStringAsync(ct)).ShouldBe(new string('a', BodyBytes));
    }

    /// <remarks>
    /// The framework's own <c>*, Accept-Encoding</c> is out of the provider's reach, so it is named here rather than
    /// asserted as correct (ADR-020).
    /// </remarks>
    [Fact]
    public async Task A_wildcard_vary_from_the_destination_gains_no_cache_control()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await Get($"{CompressibleRoute}&{StubDestination.VaryQuery}=*", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Vary.ShouldContain("*", "the destination's wildcard survives the hop");
        response.Headers.Vary.ShouldNotContain(
            "Cache-Control",
            "a wildcard already covers every dimension, so narrowing it with a field name says nothing");
    }

    [Fact]
    public async Task An_existing_cache_control_vary_is_not_duplicated()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response =
            await Get($"{CompressibleRoute}&{StubDestination.VaryQuery}=Cache-Control", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Vary.Count(v => string.Equals(v, "Cache-Control", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(1);
    }

    private static string Declaring(string encoding) =>
        $"{CompressibleRoute}&{StubDestination.ContentEncodingQuery}={encoding}";

    private async Task<HttpResponseMessage> Get(string route, CancellationToken ct)
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, route);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        return await client.SendAsync(request, ct);
    }

    private static string Decompress(byte[] encoded)
    {
        using MemoryStream source = new(encoded);
        using GZipStream decompressor = new(source, CompressionMode.Decompress);
        using StreamReader reader = new(decompressor, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
