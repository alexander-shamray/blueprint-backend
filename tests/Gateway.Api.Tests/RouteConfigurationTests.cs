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

/// <summary>
/// PR-17's headline deliverable (Appendix C): the two assertions over
/// <c>ReverseProxy:Routes</c> that nothing else in the solution can make.
/// </summary>
/// <remarks>
/// <para>
/// The in-process API tests of §12.4 call each service directly, on the path
/// it maps, so they exercise everything after the prefix strip and nothing
/// before it. Path composition is gateway configuration and this is the only
/// suite that sees it.
/// </para>
/// <para>
/// Policy resolution is the other half, and at the pinned YARP it is louder
/// than the blueprint described — the host refuses to start rather than
/// dropping the route, which <see cref="UnresolvablePolicyTests"/> measures
/// and the chapters were amended for. What that leaves for this class is
/// everything YARP does <i>not</i> validate: it has no opinion on whether a
/// route carries a rate limiter at all, on which prefix a route strips, or on
/// whether the path it forwards is one the service behind it serves.
/// </para>
/// </remarks>
public sealed class RouteConfigurationTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    /// <summary>
    /// The two policy names YARP resolves itself rather than through
    /// <c>IAuthorizationPolicyProvider</c> (§10.2).
    /// </summary>
    /// <remarks>
    /// <c>anonymous</c> is <c>AllowAnonymous</c> and <c>default</c> is the
    /// framework's default policy. Neither is registered anywhere, so a
    /// resolution test that did not subtract them would fail on a route file
    /// that is correct — and registering a policy called "anonymous" to make it
    /// pass would register one that never runs, since YARP intercepts the name
    /// before the provider sees it.
    /// </remarks>
    private static readonly string[] YarpReserved = ["anonymous", "default"];

    /// <summary>
    /// What each service serves, as the group its endpoints map (§4.2's
    /// composition root). Hand-written, one entry per cluster, on the same
    /// terms as <c>ContractSamples</c> in <c>Platform.IntegrationTests</c>:
    /// both directions are asserted, so an entry cannot rot into a comment and
    /// a cluster cannot arrive without one.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than read from the services, because reading them
    /// would mean this project referencing every service — the coupling §10.1
    /// exists to prevent, in test clothing. Catalog's entry is the one with a
    /// file behind it today: <c>ProductEndpoints</c> maps
    /// <c>/v1/catalog/products</c>, and its own comment names this assertion.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> ServiceGroups =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["catalog"] = "/v1/catalog/products",
            ["ordering"] = "/v1/orders",
            ["inventory"] = "/v1/inventory",
            ["payments"] = "/v1/payments",

            // The BFF is a second namespace rather than a service under the
            // first (§10.2), so /bff is stripped whole and everything it serves
            // is under the root it receives.
            ["web-bff"] = "/"
        };

    /// <summary>
    /// The first of Appendix C's two, in the strongest form available: not
    /// "the names in the file resolve" but "the host accepted every route in
    /// the file". <see cref="IProxyStateLookup"/> is YARP's own answer, so a
    /// missing cluster, a malformed match and an unresolvable policy all
    /// report here as one thing — an id that went in and did not come out.
    /// </summary>
    /// <remarks>
    /// The policy half of that reaches this assertion only if a future release
    /// goes back to dropping rather than throwing; today an unresolvable name
    /// fails every test in the project, including this one, before it can be
    /// counted. Which is the right guard either way, and the reason the
    /// assertion is written against the lookup rather than against a startup
    /// exception: it states what must be true, not which mechanism enforces it.
    /// </remarks>
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

    /// <summary>
    /// The resolution itself, asserted separately from the drop above because
    /// the two fail differently: this one names the policy that could not be
    /// found, where the lookup can only say which id vanished.
    /// </summary>
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

        // §11.4's guard against a vacuous pass: over a route file naming no
        // policy at all, "every name resolves" is true and worthless. Sharper
        // than it was, too — the reserved names are subtracted above, so this
        // would now also catch a file in which every route had been switched
        // to "anonymous".
        named.ShouldNotBeEmpty();

        foreach (string policy in named.Distinct(StringComparer.Ordinal))
        {
            AuthorizationPolicy? resolved = await policies.GetPolicyAsync(policy);

            resolved.ShouldNotBeNull(
                $"'{policy}' is named by a route and registered nowhere — AddCommonWebDefaults holds 'authenticated' " +
                "and Program.cs holds the gateway's own (§10.2)");
        }
    }

    /// <summary>
    /// The authorization counterpart of the rate-limiter invariant below, and
    /// the edge half of §11.4's deny-by-default.
    /// </summary>
    /// <remarks>
    /// <c>AddCommonWebDefaults</c> sets a fallback policy, so a route naming no
    /// policy is no longer public — it inherits "authenticated" and fails
    /// closed rather than open. That makes this test a readability rule rather
    /// than a security one, and it is worth having anyway: a public path by
    /// omission and a public path by decision read identically in a route file,
    /// and the person deciding whether a path should be public is reading this
    /// file rather than <c>Common.Web</c>.
    /// </remarks>
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

    /// <summary>
    /// §10.2's stated invariant, and the one it says to assert rather than
    /// review for: YARP applies no limit when the property is absent, so a
    /// route opts out of §10.1's rate limiting by omission.
    /// </summary>
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
    /// Both directions against <see cref="GatewayRateLimiterPolicies.All"/>,
    /// which is the registration's only witness — the rate limiter's policy map
    /// is internal to the framework, so unlike an authorization policy there is
    /// no provider to ask.
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

    /// <summary>
    /// The strip is a property of the namespace, not of the service: every
    /// route under <c>/api</c> removes <c>/api</c> and the one under
    /// <c>/bff</c> removes <c>/bff</c>. Stripping <c>/api/v1</c> on one route
    /// and <c>/api</c> on another is §10.2's dual-version trap, and it works in
    /// whichever one was tested first.
    /// </summary>
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

    /// <summary>
    /// Appendix C's second: each route's match minus its strip against the
    /// group its service maps. A prefix rather than an equality, and Catalog is
    /// the reason — <c>/api/v1/catalog/{**catch-all}</c> strips to
    /// <c>/v1/catalog</c> while <c>ProductEndpoints</c> maps
    /// <c>/v1/catalog/products</c>, so the route carries a whole family of
    /// paths to a service that serves one of them.
    /// </summary>
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

    /// <summary>
    /// The registry's other direction. Without it an entry outlives the cluster
    /// it described and the test above keeps passing on the routes that remain.
    /// </summary>
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

    /// <summary>
    /// YARP's own view of what it applied, which is a different claim from the
    /// configuration having said it: the limiter runs off endpoint metadata,
    /// and a route whose policy did not survive config load would reach the
    /// destination unmetered.
    /// </summary>
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

        // The guard the rest of this class rests on. Every assertion below is a
        // foreach, and a foreach over nothing passes.
        routes.ShouldNotBeEmpty();

        return routes;
    }
}
