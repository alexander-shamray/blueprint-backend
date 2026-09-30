using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>The edge reaches no gRPC method, from either end (§9.7, ADR-052).</summary>
public sealed class GrpcPathTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    /// <summary>Every gRPC method the platform serves, by hand, so this project references no service host.</summary>
    private static readonly string[] MethodPaths =
    [
        "/catalog.pricing.v1.Pricing/GetPrices",
        "/ordering.delivery.v1.DeliveryAddresses/Get"
    ];

    /// <summary>The port both gRPC endpoints bind (§9.7, ADR-052).</summary>
    private const string GrpcPort = ":8081";

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task No_route_matches_a_grpc_method_path(string path)
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = new ByteArrayContent([]) };

        // Authenticated past §11.4's fallback policy, so only an unmatched path answers 404.
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            $"'{path}' is a gRPC method and §10.2 routes HTTP (ADR-052)");
    }

    public static TheoryData<string> Methods()
    {
        TheoryData<string> data = [];

        foreach (string path in MethodPaths)
            data.Add(path);

        return data;
    }

    [Fact]
    public void No_cluster_dials_a_grpc_port()
    {
        IConfiguration configuration = factory.Services.GetRequiredService<IConfiguration>();

        (string Cluster, string Address)[] destinations =
        [
            .. configuration.GetSection("ReverseProxy:Clusters").GetChildren()
                .SelectMany(cluster => cluster.GetSection("Destinations").GetChildren()
                    .Select(destination => (Cluster: cluster.Key, Address: destination["Address"] ?? string.Empty)))
        ];

        // Over an empty set the loop passes, which is what a renamed section produces.
        destinations.ShouldNotBeEmpty();

        foreach ((string cluster, string address) in destinations)
        {
            address.ShouldNotContain(
                GrpcPort,
                Case.Sensitive,
                $"cluster '{cluster}' dials a gRPC port; the proxy speaks HTTP/1.1 to its destinations " +
                "and an Http2-only endpoint answers that with a 400 (§9.7)");
        }
    }
}
