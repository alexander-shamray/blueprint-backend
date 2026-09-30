using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>Behind an HTTPS ingress, compression reads the forwarded scheme, not the hop's (ADR-020).</summary>
public sealed class ForwardedSchemeCompressionTests(StubDestination stub) : IClassFixture<StubDestination>
{
    private const int BodyBytes = 8192;

    /// <summary>From the documentation range (RFC 5737).</summary>
    private const string ForwardedClient = "203.0.113.9";

    private static readonly string CompressibleRoute =
        $"/api/v1/catalog/products?{StubDestination.BodySizeQuery}={BodyBytes}";

    [Fact]
    public async Task A_request_forwarded_as_https_is_still_compressed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using BehindHttpsIngressFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        client.BaseAddress.ShouldNotBeNull();
        client.BaseAddress!.Scheme.ShouldBe(
            Uri.UriSchemeHttp,
            "the hop itself is plain — the ingress terminated TLS, which is the topology §10.1 describes");

        using HttpRequestMessage request = new(HttpMethod.Get, CompressibleRoute);
        request.Headers.Add("X-Forwarded-For", ForwardedClient);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBe(
            ["gzip"],
            "UseForwardedHeaders made Request.IsHttps true before the compression decision was taken, so " +
            "the framework default would have refused this response and the edge would compress nothing");
    }

    /// <summary>The gateway behind an ingress that forwards the scheme it terminated (§15.3).</summary>
    private sealed class BehindHttpsIngressFactory(string destination) : StubbedGatewayFactory(destination)
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            .. base.AdditionalSettings,
            new("Ingress:Enabled", "true"),
            new("Ingress:TrustedNetworks:0", "127.0.0.1/32")
        ];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter, LoopbackPeerStartupFilter>());
        }
    }

    /// <summary>A peer set ahead of <c>UseForwardedHeaders</c>, which honours no header from a null one.</summary>
    private sealed class LoopbackPeerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, following) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Loopback;

                    await following();
                });

                next(app);
            };
    }
}
