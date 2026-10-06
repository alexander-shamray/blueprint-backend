using Inventory.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// §11.4's callout: every endpoint policy name, read off the built endpoints, resolves through the provider.
/// </summary>
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

        // Not vacuous: losing every RequireAuthorization line would empty the set.
        named.ShouldContain(InventoryPermissions.Admin);

        foreach (string policy in named)
        {
            AuthorizationPolicy? resolved = await policies.GetPolicyAsync(policy);

            resolved.ShouldNotBeNull(
                $"'{policy}' is named by an endpoint and registered nowhere — the endpoint " +
                "throws on the first request that reaches it, never at startup (§11.4)");
        }
    }

    [Fact]
    public async Task The_shared_authenticated_policy_is_registered_by_common_web()
    {
        // Named by no Inventory endpoint but by the gateway's routes (§10.2), resolved through this provider.
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        (await policies.GetPolicyAsync("authenticated")).ShouldNotBeNull();
    }

    [Fact]
    public void No_inventory_endpoint_is_anonymous()
    {
        // A stock level or a reservation is never public, unlike Catalog's listing.
        string[] names = ["SetOnHand", "GetStock", "GetReservation", "ReleaseReservation", "ReinstateReservation"];

        foreach (Endpoint endpoint in Endpoints.Where(e => names.Contains(Name(e))))
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(Name(endpoint));
            endpoint.Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Policy)
                .ShouldContain(InventoryPermissions.Admin);
        }

        // Not vacuous: a renamed endpoint would drop out of the loop above.
        Endpoints.Count(e => names.Contains(Name(e))).ShouldBe(names.Length);
    }

    private static string? Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
}
