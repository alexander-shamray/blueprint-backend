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
    /// <summary>§10.3's anonymous fixed window.</summary>
    private const int PermitLimit = 100;

    private const string PublicRoute = "/api/v1/catalog/products";

    /// <summary>Two addresses from the documentation range (RFC 5737).</summary>
    private const string FirstClient = "203.0.113.7";
    private const string SecondClient = "203.0.113.8";

    [Fact]
    public async Task A_trusted_forwarded_address_is_what_the_limiter_partitions_on()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using BehindIngressFactory factory = new(stub.Address);
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

    private static async Task<HttpResponseMessage> Get(HttpClient client, string forwardedFor, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, PublicRoute);
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        return await client.SendAsync(request, ct);
    }

    /// <summary>The gateway as it runs in Kubernetes (§15.3): a proxy in front, only that proxy trusted.</summary>
    private sealed class BehindIngressFactory(string destination) : StubbedGatewayFactory(destination)
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            .. base.AdditionalSettings,
            new("Ingress:Enabled", "true"),

            // Loopback, the peer the startup filter below gives every request.
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
