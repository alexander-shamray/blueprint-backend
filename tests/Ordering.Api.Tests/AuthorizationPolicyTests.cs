using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§11.4's callout: every policy an endpoint names, read off the built endpoints, resolves.</summary>
public class AuthorizationPolicyTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    private IEnumerable<Endpoint> Endpoints =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    [Fact]
    public async Task Every_policy_an_endpoint_names_resolves()
    {
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        string[] named =
        [
            .. Endpoints
                .SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(a => a.Policy)
                .OfType<string>()
                .Distinct()
        ];

        // Not vacuous: the loop below passes trivially over a set missing a policy.
        named.ShouldContain(OrderingPermissions.Write);
        named.ShouldContain(OrderingPermissions.Cancel);
        named.ShouldContain(
            OrderingPermissions.DeliveryAddress,
            "ADR-052's method is the first in this service behind a policy no endpoint route names");

        foreach (string policy in named)
        {
            AuthorizationPolicy? resolved = await policies.GetPolicyAsync(policy);

            resolved.ShouldNotBeNull(
                $"'{policy}' is named by an endpoint and registered nowhere — the endpoint " +
                "throws on the first request that reaches it, never at startup (§11.4)");
        }
    }

    [Fact]
    public async Task Orders_admin_is_a_claim_and_is_deliberately_not_a_policy()
    {
        // §11.4: CancelOrderHandler reads the claim against a loaded aggregate, which no policy could. A literal,
        // since OrderingPermissions holds policies alone.
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        (await policies.GetPolicyAsync("orders:admin")).ShouldBeNull();

        Endpoints
            .SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>())
            .Select(a => a.Policy)
            .ShouldNotContain("orders:admin");
    }

    [Fact]
    public async Task The_shared_authenticated_policy_is_registered_by_common_web()
    {
        // Named by no Ordering endpoint but by the gateway's ordering route (§10.2).
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        (await policies.GetPolicyAsync("authenticated")).ShouldNotBeNull();
    }

    [Fact]
    public void No_ordering_endpoint_is_anonymous()
    {
        // An order belongs to somebody, and an AllowAnonymous defeats every policy on its path.
        foreach (Endpoint endpoint in Endpoints.Where(e => Name(e) is "PlaceOrder" or "CancelOrder"))
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(Name(endpoint));

        // Not vacuous: the loop above passes over the empty set a renamed endpoint would leave.
        Endpoints.Count(e => Name(e) is "PlaceOrder" or "CancelOrder").ShouldBe(2);
    }

    private static string? Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
}
