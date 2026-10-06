using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;

namespace Gateway.Api.Tests;

/// <summary>The assertions over <c>ReverseProxy:Routes</c> (§12.4).</summary>
public sealed class RouteConfigurationTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    /// <summary>Names YARP resolves itself rather than through <c>IAuthorizationPolicyProvider</c> (§10.2).</summary>
    private static readonly string[] YarpReserved = ["anonymous", "default"];

    /// <summary>The group each cluster's service maps, by hand so this project references no service.</summary>
    private static readonly IReadOnlyDictionary<string, string> ServiceGroups =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["catalog"] = "/v1/catalog/products",
            ["ordering"] = "/v1/orders",
            ["inventory"] = "/v1/inventory",
            ["payments"] = "/v1/payments",

            // A second namespace (§10.2), stripped whole, so the BFF serves under the root it receives.
            ["web-bff"] = "/"
        };

    /// <summary><see cref="IProxyStateLookup"/> is YARP's answer, so a dropped route shows as a missing id.</summary>
    [Fact]
    public void Every_route_in_the_file_is_a_route_the_proxy_accepted()
    {
        IReadOnlyList<RouteConfiguration> configured = ReadRoutes();
        IProxyStateLookup lookup = factory.Services.GetRequiredService<IProxyStateLookup>();

        string[] accepted = [.. lookup.GetRoutes().Select(r => r.Config.RouteId).Order(StringComparer.Ordinal)];

        accepted.ShouldBe(
            [.. configured.Select(r => r.Id).Order(StringComparer.Ordinal)],
            "a route in the file that the proxy did not accept is a path that stopped existing (§10.2)");
    }

    /// <summary>The clusters YARP accepted, so a new one cannot arrive without §10.2's check.</summary>
    [Fact]
    public void Every_cluster_is_health_checked_against_readiness()
    {
        ClusterState[] clusters = [.. factory.Services.GetRequiredService<IProxyStateLookup>().GetClusters()];
        clusters.ShouldNotBeEmpty("with no cluster accepted there is nothing to hold to the rule");

        string[] unprobed =
        [
            .. clusters
                .Where(c => c.Model.Config.HealthCheck?.Active is not { Enabled: true, Path: "/health/ready" } ||
                    c.Model.Config.LoadBalancingPolicy != "PowerOfTwoChoices")
                .Select(c => c.ClusterId)
                .Order(StringComparer.Ordinal)
        ];

        unprobed.ShouldBeEmpty("a cluster YARP does not probe keeps routing to a pod that is failing (§10.2)");
    }

    /// <summary>Names the policy that could not be found, where the lookup can only say which id vanished.</summary>
    [Fact]
    public async Task Every_authorization_policy_named_resolves()
    {
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        string[] named =
        [
            .. ReadRoutes()
                .Select(r => r.AuthorizationPolicy)
                .Where(p => p is not null && !YarpReserved.Contains(p, StringComparer.OrdinalIgnoreCase))
                .Select(p => p!)
        ];

        // Over a file naming no policy but reserved ones, "every name resolves" is true and worthless (§11.4).
        named.ShouldNotBeEmpty();

        foreach (string policy in named.Distinct(StringComparer.Ordinal))
        {
            AuthorizationPolicy? resolved = await policies.GetPolicyAsync(policy);

            resolved.ShouldNotBeNull(
                $"'{policy}' is named by a route and registered nowhere — AddCommonWebDefaults holds 'authenticated' " +
                "and Program.cs holds the gateway's own (§10.2)");
        }
    }

    /// <summary>A readability rule, since §11.4's fallback fails an omission closed (§10.2).</summary>
    [Fact]
    public void Every_route_names_an_authorization_policy()
    {
        foreach (RouteConfiguration route in ReadRoutes())
        {
            route.AuthorizationPolicy.ShouldNotBeNullOrWhiteSpace(
                $"route '{route.Id}' names no AuthorizationPolicy, so whether it is public is a question about " +
                "Common.Web's fallback rather than about this file (§10.2, §11.4)");
        }
    }

    /// <summary>§10.2's invariant, since YARP applies no limit when the property is absent.</summary>
    [Fact]
    public void Every_route_names_a_rate_limiter_policy()
    {
        foreach (RouteConfiguration route in ReadRoutes())
        {
            route.RateLimiterPolicy.ShouldNotBeNullOrWhiteSpace(
                $"route '{route.Id}' carries no RateLimiterPolicy, so it is unlimited — including the admin ones, " +
                "because an authorised client with a broken retry loop is still a flood (§10.2)");
        }
    }

    /// <summary>
    /// Against <see cref="GatewayRateLimiterPolicies.All"/>, since the limiter's policy map has no provider to ask.
    /// </summary>
    [Fact]
    public void The_rate_limiter_policies_named_and_the_ones_registered_are_the_same_set()
    {
        string[] named =
        [
            .. ReadRoutes()
                .Select(r => r.RateLimiterPolicy)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        named.ShouldBe(
            [.. GatewayRateLimiterPolicies.All.Order(StringComparer.Ordinal)],
            "a route may name no policy outside this set, and a registered policy no route names is a registration " +
            "with no reader — the defect §11.4 names for an unused authorization policy");
    }

    /// <summary>The strip belongs to the namespace, not the service, against §10.2's dual-version trap.</summary>
    [Fact]
    public void Every_route_strips_exactly_the_namespace_it_matches()
    {
        foreach (RouteConfiguration route in ReadRoutes())
        {
            route.RemovedPrefixes.ShouldBe(
                [route.Namespace],
                $"route '{route.Id}' matches under '{route.Namespace}' — one strip per namespace, that one (§10.2)");
        }
    }

    /// <summary>A prefix, not an equality, since the catalog routes carry a family of paths to one group.</summary>
    [Fact]
    public void Every_route_forwards_a_path_its_service_serves()
    {
        foreach (RouteConfiguration route in ReadRoutes())
        {
            ServiceGroups.ShouldContainKey(route.ClusterId);

            string group = ServiceGroups[route.ClusterId];
            string forwarded = route.ForwardedPathPrefix;

            bool serves =
                group.Equals(forwarded, StringComparison.Ordinal) ||
                group.StartsWith(forwarded.TrimEnd('/') + "/", StringComparison.Ordinal);

            serves.ShouldBeTrue(
                $"route '{route.Id}' forwards '{forwarded}' and {route.ClusterId} maps '{group}' — the version sits " +
                "before the resource and the service never sees the namespace prefix (§10.2)");
        }
    }

    /// <summary>The other direction, without which an entry outlives the cluster it described.</summary>
    [Fact]
    public void Every_service_group_entry_names_a_cluster_a_route_uses()
    {
        string[] routed =
        [
            .. ReadRoutes()
                .Select(r => r.ClusterId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        routed.ShouldBe([.. ServiceGroups.Keys.Order(StringComparer.Ordinal)]);
    }

    /// <summary>The endpoint metadata the limiter runs off, rather than what the configuration said.</summary>
    [Fact]
    public void Every_proxy_endpoint_carries_the_rate_limiter_policy_its_route_names()
    {
        EndpointDataSource endpoints = factory.Services.GetRequiredService<EndpointDataSource>();

        Endpoint[] proxied = [.. endpoints.Endpoints.Where(e => e.Metadata.GetMetadata<RouteModel>() is not null)];

        proxied.ShouldNotBeEmpty();

        foreach (Endpoint endpoint in proxied)
        {
            RouteModel route = endpoint.Metadata.GetMetadata<RouteModel>()!;
            EnableRateLimitingAttribute? limiter = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();

            limiter.ShouldNotBeNull($"route '{route.Config.RouteId}' reached the pipeline with no limiter metadata");
            limiter.PolicyName.ShouldBe(route.Config.RateLimiterPolicy);
        }
    }

    private IReadOnlyList<RouteConfiguration> ReadRoutes()
    {
        IReadOnlyList<RouteConfiguration> routes =
            RouteConfiguration.ReadAll(factory.Services.GetRequiredService<IConfiguration>());

        // A foreach over nothing passes.
        routes.ShouldNotBeEmpty();

        return routes;
    }
}
