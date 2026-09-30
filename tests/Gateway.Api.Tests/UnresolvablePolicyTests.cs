using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>A route naming a policy nobody registered stops the gateway at startup (§10.2).</summary>
public sealed class UnresolvablePolicyTests
{
    [Fact]
    public void A_route_naming_an_unregistered_authorization_policy_refuses_to_start()
    {
        using UnresolvableAuthorizationPolicyFactory factory = new();

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        // ToString, since the naming is done by the ArgumentException two levels in.
        thrown.ToString().ShouldContain("no-such-policy");
        thrown.ToString().ShouldContain("broken");
    }

    /// <summary>The other registry, which §10.2 keeps apart and different code validates.</summary>
    [Fact]
    public void A_route_naming_an_unregistered_rate_limiter_policy_refuses_to_start()
    {
        using UnresolvableRateLimiterPolicyFactory factory = new();

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(() => _ = factory.Services);

        thrown.ToString().ShouldContain("no-such-policy");
        thrown.ToString().ShouldContain("broken");
    }

    private sealed class UnresolvableAuthorizationPolicyFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("ReverseProxy:Routes:broken:ClusterId", "catalog"),
            new("ReverseProxy:Routes:broken:Match:Path", "/api/v1/broken/{**catch-all}"),
            new("ReverseProxy:Routes:broken:AuthorizationPolicy", "no-such-policy"),
            new("ReverseProxy:Routes:broken:RateLimiterPolicy", GatewayRateLimiterPolicies.Anonymous)
        ];
    }

    private sealed class UnresolvableRateLimiterPolicyFactory : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            new("ReverseProxy:Routes:broken:ClusterId", "catalog"),
            new("ReverseProxy:Routes:broken:Match:Path", "/api/v1/broken/{**catch-all}"),
            new("ReverseProxy:Routes:broken:RateLimiterPolicy", "no-such-policy")
        ];
    }
}
