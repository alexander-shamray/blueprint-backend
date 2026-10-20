using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>A route an operator switches off answers §10.5's 503 at the edge, each one (ADR-116).</summary>
public sealed class SuspendedRouteTests(StubDestination stub) : IClassFixture<StubDestination>
{
    /// <summary>Read from the shipped file, so a route added later is covered without a line here.</summary>
    [Fact]
    public async Task Every_route_to_a_suspendable_cluster_answers_the_503_and_forwards_nothing()
    {
        RouteConfiguration[] routes =
            [.. ReadRoutes().Where(r => SuspendedRoute.SuspendableClusters.Contains(r.ClusterId))];
        routes.ShouldNotBeEmpty("with no route to a suspendable cluster there is nothing to hold to the rule");

        foreach (RouteConfiguration route in routes)
        {
            using SwitchedOffFactory factory = new(stub.Address, route.Id);
            using HttpClient client = factory.CreateClient();
            int before = stub.ReceivedPaths.Count;

            using HttpResponseMessage response =
                await client.SendAsync(Request(route), TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, route.Id);
            response.Headers.RetryAfter!.Delta.ShouldBe(SuspendedRoute.RetryAfter, route.Id);
            stub.ReceivedPaths.Count.ShouldBe(before, $"{route.Id} forwarded a request it had switched off");

            using JsonDocument body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            body.RootElement.GetProperty("code").GetString().ShouldBe(SuspendedRoute.Code, route.Id);
            body.RootElement.GetProperty("status").GetInt32().ShouldBe(503, route.Id);
        }
    }

    /// <summary>The same request without the switch, or the test above could pass on a route that never
    /// proxied.</summary>
    [Fact]
    public async Task A_route_not_switched_off_still_forwards()
    {
        RouteConfiguration route = ReadRoutes().First(r => SuspendedRoute.SuspendableClusters.Contains(r.ClusterId));
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();
        int before = stub.ReceivedPaths.Count;

        using HttpResponseMessage response =
            await client.SendAsync(Request(route), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        stub.ReceivedPaths.Count.ShouldBe(before + 1);
    }

    /// <summary>ADR-104 keeps four services on, so a route to one cannot carry the switch.</summary>
    [Fact]
    public void Every_route_to_any_other_cluster_refuses_the_switch_at_startup()
    {
        RouteConfiguration[] routes =
            [.. ReadRoutes().Where(r => !SuspendedRoute.SuspendableClusters.Contains(r.ClusterId))];
        routes.ShouldNotBeEmpty("with no other route there is nothing to hold to the rule");

        foreach (RouteConfiguration route in routes)
        {
            using SwitchedOffFactory factory = new(stub.Address, route.Id);

            InvalidOperationException thrown =
                Should.Throw<InvalidOperationException>(() => _ = factory.Services, route.Id);

            // ToString, since YARP wraps the filter's exception.
            thrown.ToString().ShouldContain(route.Id);
            thrown.ToString().ShouldContain("ADR-104");
        }
    }

    private static IReadOnlyList<RouteConfiguration> ReadRoutes()
    {
        using GatewayFactory factory = new();

        return RouteConfiguration.ReadAll(factory.Services.GetRequiredService<IConfiguration>());
    }

    /// <summary>A caller holding the route's own permission, so the 503 is not a 401 or a 403.</summary>
    private static HttpRequestMessage Request(RouteConfiguration route)
    {
        string path = route.MatchPath.Replace("{**catch-all}", "x", StringComparison.Ordinal);
        HttpRequestMessage request = new(new HttpMethod(route.Methods is [string method, ..] ? method : "GET"), path);

        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");
        request.Headers.Add(TestAuthHandler.PermissionsHeader, route.AuthorizationPolicy ?? string.Empty);

        return request;
    }

    private sealed class SwitchedOffFactory(string destination, string routeId) : StubbedGatewayFactory(destination)
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            .. base.AdditionalSettings,
            new($"ReverseProxy:Routes:{routeId}:Metadata:{SuspendedRoute.MetadataKey}", bool.TrueString)
        ];
    }
}
