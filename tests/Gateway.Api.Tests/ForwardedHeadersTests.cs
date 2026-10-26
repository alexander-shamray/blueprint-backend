using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>§4.2's forwarded headers: a trusted <c>X-Forwarded-For</c> is what §10.3 partitions on.</summary>
public sealed class ForwardedHeadersTests(StubDestination stub) : IClassFixture<StubDestination>
{
    /// <summary>§10.3's anonymous fixed window, at its default.</summary>
    private static readonly int PermitLimit = new GatewayRateLimitOptions().AnonymousPermitLimit;

    private const string PublicRoute = "/api/v1/catalog/products";

    /// <summary>Two addresses from the documentation range (RFC 5737).</summary>
    private const string FirstClient = "203.0.113.7";
    private const string SecondClient = "203.0.113.8";

    /// <summary>The ingress's network, and an address in it that is not loopback, which is trusted by
    /// default.</summary>
    private const string IngressNetwork = "10.0.0.0/8";
    private static readonly IPAddress Ingress = IPAddress.Parse("10.1.2.3");

    [Fact]
    public async Task A_trusted_forwarded_address_is_what_the_limiter_partitions_on()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using BehindIngressFactory factory = new(stub.Address, Ingress);
        using HttpClient client = factory.CreateClient();

        for (int i = 0; i < PermitLimit; i++)
        {
            HttpResponseMessage permitted = await Get(client, FirstClient, ct);

            permitted.StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                $"request {i + 1} of {PermitLimit} is inside {FirstClient}'s window");
        }

        HttpResponseMessage exhausted = await Get(client, FirstClient, ct);

        exhausted.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "the window is spent for this address, which is what makes the next assertion mean anything");

        HttpResponseMessage other = await Get(client, SecondClient, ct);

        other.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            $"{SecondClient} holds its own window — a gateway ignoring the forwarded header would meter " +
            "both addresses as the one connection it can see (§10.1)");
    }

    [Theory]
    [InlineData("198.51.100.9")]
    [InlineData("127.0.0.1")]
    public async Task A_forwarded_address_from_a_peer_outside_the_trusted_networks_is_ignored(string peer)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Loopback is trusted by default, so it is ignored here only if the list replaced the defaults.
        using BehindIngressFactory factory = new(stub.Address, IPAddress.Parse(peer));
        using HttpClient client = factory.CreateClient();

        for (int i = 0; i < PermitLimit; i++)
            (await Get(client, FirstClient, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage other = await Get(client, SecondClient, ct);

        other.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            $"{peer} is not the ingress, so a client's own header cannot choose its partition (§10.3)");
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string forwardedFor, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, PublicRoute);
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        return await client.SendAsync(request, ct);
    }

    /// <summary>The gateway as it runs in Kubernetes (§15.3): a proxy in front, only that proxy trusted.</summary>
    private sealed class BehindIngressFactory(string destination, IPAddress peer) : StubbedGatewayFactory(destination)
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            .. base.AdditionalSettings,
            new("Ingress:Enabled", "true"),

            // The one network that is the ingress; the startup filter below gives every request its own peer.
            new("Ingress:TrustedNetworks:0", IngressNetwork)
        ];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new PeerStartupFilter(peer)));
        }
    }

    /// <summary>A peer set ahead of <c>UseForwardedHeaders</c>, which honours no header from a null one.</summary>
    private sealed class PeerStartupFilter(IPAddress peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, following) =>
                {
                    context.Connection.RemoteIpAddress = peer;

                    await following();
                });

                next(app);
            };
    }
}
