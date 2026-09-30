using System.Net;
using Common.Web;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>§4.2's conditional blocks, forwarded headers and CORS: optional, and required once switched on.</summary>
public sealed class ConditionalBlockTests
{
    [Fact]
    public void Cors_enabled_with_no_origins_refuses_to_start()
    {
        using CorsWithoutOriginsFactory factory = new();

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        thrown.Message.ShouldContain("Cors:Origins");
    }

    [Fact]
    public async Task Cors_enabled_with_origins_answers_a_browser_from_one_of_them()
    {
        using CorsFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", CorsFactory.Origin);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(CorsFactory.Origin);

        // Here and not on a preflight, whose answer never carries Expose-Headers.
        string[] exposed = [.. response.Headers.GetValues("Access-Control-Expose-Headers")];
        exposed.ShouldContain(h => h.Contains("Retry-After", StringComparison.OrdinalIgnoreCase));
        exposed.ShouldContain(h => h.Contains(CorrelationIdExtensions.Header, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A preflight carries no token, so CORS answers it before authorization runs (§4.2).</summary>
    [Fact]
    public async Task Cors_answers_a_preflight_for_a_proxied_route_that_requires_a_token()
    {
        using CorsFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage preflight = new(HttpMethod.Options, "/api/v1/orders/018f4c2e");
        preflight.Headers.Add("Origin", CorsFactory.Origin);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization");

        HttpResponseMessage response = await client.SendAsync(preflight, TestContext.Current.CancellationToken);

        // The status itself, since a browser rejects a preflight on any non-success status.
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(CorsFactory.Origin);
        response.Headers.GetValues("Access-Control-Allow-Methods").ShouldContain(
            m => m.Contains("GET", StringComparison.Ordinal));
        response.Headers.GetValues("Access-Control-Allow-Headers").ShouldContain(
            h => h.Contains("authorization", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A preflight is an <c>OPTIONS</c>, a method <c>catalog-public</c> does not match (§10.2).</summary>
    [Fact]
    public async Task Cors_answers_a_preflight_for_the_method_limited_public_route()
    {
        using CorsFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage preflight = new(HttpMethod.Options, "/api/v1/catalog/products");
        preflight.Headers.Add("Origin", CorsFactory.Origin);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");

        HttpResponseMessage response = await client.SendAsync(preflight, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(CorsFactory.Origin);
    }

    [Fact]
    public async Task Cors_left_off_answers_the_same_browser_with_no_header()
    {
        using GatewayFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", CorsFactory.Origin);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    /// <summary><c>Cors__Origins__0=</c> binds one empty string, which <c>GetRequiredSection</c> cannot see.</summary>
    [Fact]
    public void Cors_enabled_with_a_blank_origin_refuses_to_start()
    {
        using CorsWithBlankOriginFactory factory = new();

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        thrown.Message.ShouldContain("Cors:Origins");
    }

    /// <summary>A non-blank value that is no browser origin, which <c>WithOrigins</c> would take literally.</summary>
    [Theory]
    [InlineData("https//spa.example")]
    // The browser's Origin header never carries a trailing slash.
    [InlineData("https://spa.example/")]
    [InlineData("https://spa.example/app")]
    [InlineData("https://user:password@spa.example")]
    // A browser serialises an origin without its scheme's default port.
    [InlineData("https://spa.example:443")]
    [InlineData("http://spa.example:80")]
    // The canonical form is lowercase.
    [InlineData("https://SPA.example")]
    public void Cors_enabled_with_a_value_that_is_not_an_origin_refuses_to_start(string configured)
    {
        using CorsWithOriginFactory factory = new(configured);

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        thrown.Message.ShouldContain("index 0");

        // A rejected shape carries credentials, and §13.4's redactor cannot see a secret inside a message.
        thrown.Message.ShouldNotContain(configured);
    }

    /// <summary>A wildcard is ruled out by <c>AllowCredentials</c>, and named, as it holds no secret.</summary>
    [Fact]
    public void Cors_enabled_with_a_wildcard_origin_refuses_to_start()
    {
        using CorsWithOriginFactory factory = new("*");

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        thrown.Message.ShouldContain("AllowCredentials");
    }

    [Fact]
    public void Ingress_enabled_with_no_trusted_networks_refuses_to_start()
    {
        using IngressWithoutNetworksFactory factory = new();

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        // Left empty, only loopback is trusted, so anonymous callers share one §10.3 partition.
        thrown.Message.ShouldContain("Ingress:TrustedNetworks");
    }

    /// <summary>Pins the parse to startup.</summary>
    [Fact]
    public void Ingress_enabled_with_an_unparseable_network_refuses_to_start() =>
        Should.Throw<FormatException>(() =>
        {
            using IngressWithBadNetworkFactory factory = new();

            _ = factory.Services;
        });

    private sealed class CorsFactory : GatewayFactory
    {
        /// <summary>The SPA's dev origin (§14.1) — 5173 is Vite's default.</summary>
        public const string Origin = "http://localhost:5173";

        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("Cors:Enabled", "true"),
            new("Cors:Origins:0", Origin)
        ];
    }

    private sealed class CorsWithoutOriginsFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
            [new("Cors:Enabled", "true")];
    }

    private sealed class CorsWithBlankOriginFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("Cors:Enabled", "true"),
            new("Cors:Origins:0", string.Empty)
        ];
    }

    private sealed class CorsWithOriginFactory(string origin) : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("Cors:Enabled", "true"),
            new("Cors:Origins:0", origin)
        ];
    }

    private sealed class IngressWithoutNetworksFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
            [new("Ingress:Enabled", "true")];
    }

    private sealed class IngressWithBadNetworkFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("Ingress:Enabled", "true"),
            new("Ingress:TrustedNetworks:0", "10.0.0.0/8"),
            new("Ingress:TrustedNetworks:1", "not-a-network")
        ];
    }
}
