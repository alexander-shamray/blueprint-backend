using System.Net;
using System.Text.Json;
using Common.Web;
using Gateway.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;

namespace Gateway.Api.Tests;

/// <summary>The edge's own deadline, the outermost of §9.7's layers, read off the built host.</summary>
public sealed class RequestTimeoutTests(StubDestination stub) : IClassFixture<StubDestination>
{
    [Fact]
    public void The_edge_waits_longer_than_a_service_and_less_than_its_own_drain()
    {
        using GatewayFactory factory = new();
        using HttpClient client = factory.CreateClient();

        TimeSpan deadline = factory.Services
            .GetRequiredService<IOptions<RequestTimeoutOptions>>().Value.DefaultPolicy!.Timeout!.Value;

        deadline.ShouldBe(GatewayLimits.RequestTimeout);

        // Decreasing inwards (§9.7): a service's 504 reaches the client before the edge gives up on it.
        deadline.ShouldBeGreaterThan(ServiceOptions.OperationTimeout);

        // §15.3: a request in flight at SIGTERM has to finish inside the drain.
        deadline.ShouldBeLessThan(
            factory.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    /// <summary>
    /// The edge's deadline is every route's, so a stream a service ends inside its own deadline (§9.7), the BFF's
    /// order and assistant streams among them, is cut first by nothing between (ADR-108, ADR-153).
    /// </summary>
    [Fact]
    public void No_route_or_cluster_cuts_a_request_before_the_edge_deadline()
    {
        using GatewayFactory factory = new();
        IProxyStateLookup lookup = factory.Services.GetRequiredService<IProxyStateLookup>();

        RouteModel[] routes = [.. lookup.GetRoutes()];
        routes.ShouldNotBeEmpty();

        foreach (RouteModel route in routes)
        {
            route.Config.Timeout.ShouldBeNull(route.Config.RouteId);
            route.Config.TimeoutPolicy.ShouldBeNull(route.Config.RouteId);
        }

        // Unset is YARP's default activity timeout, 100 seconds, which the edge's deadline sits well inside.
        foreach (ClusterState cluster in lookup.GetClusters())
        {
            TimeSpan? activity = cluster.Model.Config.HttpRequest?.ActivityTimeout;

            if (activity is not null)
                activity.Value.ShouldBeGreaterThanOrEqualTo(GatewayLimits.RequestTimeout, cluster.ClusterId);
        }
    }

    [Fact]
    public async Task A_destination_that_never_answers_is_answered_504_with_the_timed_out_code()
    {
        using ImpatientGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/catalog/products?{StubDestination.StallQuery}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        body.RootElement.GetProperty("code").GetString().ShouldBe(RequestTimeoutExtensions.TimedOutCode);
    }

    /// <summary>The edge with a deadline short enough to wait out, keeping the shared writer.</summary>
    private sealed class ImpatientGatewayFactory(string destination) : StubbedGatewayFactory(destination)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
                services.AddCommonRequestTimeouts(TimeSpan.FromMilliseconds(300)));
        }
    }
}
