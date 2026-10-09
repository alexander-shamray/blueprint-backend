using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>§10.3's per-address partition: an IPv4 client by its address, an IPv6 one by its /64.</summary>
public sealed class RateLimitPartitionTests(StubDestination stub) : IClassFixture<StubDestination>
{
    /// <summary>The <c>anonymous</c> policy's fixed window (§10.3).</summary>
    private const int PermitLimit = 100;

    private const string PublicRoute = "/api/v1/catalog/products";

    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2:ffff:ffff:ffff:ffff")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2:8000::")]
    [InlineData("203.0.113.7", "::ffff:203.0.113.7")]
    public void Two_addresses_of_one_client_share_a_partition(string first, string second) =>
        RateLimitPartitionKey.ForAddress(IPAddress.Parse(first))
            .ShouldBe(RateLimitPartitionKey.ForAddress(IPAddress.Parse(second)));

    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1")]
    [InlineData("203.0.113.7", "203.0.113.8")]
    [InlineData("::ffff:203.0.113.7", "::203.0.113.7")]
    public void Addresses_of_two_clients_do_not(string first, string second) =>
        RateLimitPartitionKey.ForAddress(IPAddress.Parse(first))
            .ShouldNotBe(RateLimitPartitionKey.ForAddress(IPAddress.Parse(second)));

    [Fact]
    public void A_request_with_no_peer_address_shares_the_one_unknown_bucket() =>
        RateLimitPartitionKey.ForAddress(null).ShouldBe(RateLimitPartitionKey.Unknown);

    /// <summary>The window spent from one address of a /64 is spent for the next, and not for another /64.</summary>
    [Fact]
    public async Task An_ipv6_client_rotating_through_its_64_finds_its_window_spent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using PeerGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        for (int i = 0; i < PermitLimit; i++)
        {
            HttpResponseMessage permitted = await SendFrom(client, "2001:db8:1:2::1", ct);

            permitted.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"request {i + 1} is inside the window");
        }

        (await SendFrom(client, "2001:db8:1:2::2", ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await SendFrom(client, "2001:db8:1:3::1", ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<HttpResponseMessage> SendFrom(HttpClient client, string peer, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, PublicRoute);
        request.Headers.Add(PeerStartupFilter.Header, peer);

        return await client.SendAsync(request, ct);
    }

    private sealed class PeerGatewayFactory(string destination) : StubbedGatewayFactory(destination)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, PeerStartupFilter>());
        }
    }

    /// <summary>The socket peer, null on a TestServer request, set from a header only this test sends.</summary>
    private sealed class PeerStartupFilter : IStartupFilter
    {
        public const string Header = "X-Test-Peer";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, following) =>
                {
                    if (IPAddress.TryParse(context.Request.Headers[Header], out IPAddress? peer))
                        context.Connection.RemoteIpAddress = peer;

                    await following();
                });

                next(app);
            };
    }
}
