using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// The edge reaches no gRPC method, from both ends: no route matches a method
/// path, and no cluster dials a gRPC port (§9.7, ADR-052).
/// </summary>
/// <remarks>
/// §10.2 routes HTTP and the two gRPC surfaces are cluster-internal, on the
/// same footing as the SQL port. A route added under a path like these would
/// publish an authenticated-only internal call to the internet with nothing
/// else in this repository to say so.
/// </remarks>
public sealed class GrpcPathTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    /// <summary>
    /// Every gRPC method the platform serves, spelt as
    /// <c>/&lt;package&gt;.&lt;service&gt;/&lt;method&gt;</c>.
    /// </summary>
    /// <remarks>
    /// By hand, one line per method, on <c>ServiceGroups</c>' terms: reading
    /// them from the services would mean this project referencing every host,
    /// which is the coupling §10.1 exists to prevent in test clothing.
    /// </remarks>
    private static readonly string[] MethodPaths =
    [
        "/catalog.pricing.v1.Pricing/GetPrices",
        "/ordering.delivery.v1.DeliveryAddresses/Get"
    ];

    /// <summary>The ports the two gRPC endpoints bind (§9.7, ADR-052).</summary>
    private const string GrpcPort = ":8081";

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task No_route_matches_a_grpc_method_path(string path)
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = new ByteArrayContent([]) };

        // Authenticated, because §11.4's fallback policy answers 401 on every
        // path an anonymous caller asks for, routed or not. Past it, only an
        // unmatched path is 404: a route that matched would be refused at its
        // own policy or handed to the proxy, and either is a published path.
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

        // The guard the loop below rests on: over an empty set it passes and
        // says nothing, which is what a renamed configuration section produces.
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
