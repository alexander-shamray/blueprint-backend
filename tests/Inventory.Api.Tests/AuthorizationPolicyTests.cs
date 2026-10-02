using Common.Application;
using Inventory.Api;
using Inventory.Application.Reservations.Reinstate;
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
        string[] names =
        [
            "SetOnHand", "GetStock", "GetReservation", "ReleaseReservation", "ReinstateReservation"
        ];

        foreach (Endpoint endpoint in Endpoints.Where(e => names.Contains(Name(e))))
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(Name(endpoint));
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)
                .ShouldContain(InventoryPermissions.Admin);
        }

        // Not vacuous: a renamed endpoint would drop out of the loop above.
        Endpoints.Count(e => names.Contains(Name(e))).ShouldBe(names.Length);
    }

    [Fact]
    public void The_idempotent_command_is_reached_through_an_authenticated_admin_endpoint()
    {
        // §8.5: an anonymous caller keys under the shared "system" segment. Found by name, since the endpoint
        // binds a request record and no handler parameter is the command.
        Type[] declared =
        [
            .. typeof(Inventory.Application.DependencyInjection).Assembly
                .GetTypes()
                .Where(typeof(IIdempotentCommand).IsAssignableFrom)
                .Where(t => t is { IsClass: true, IsAbstract: false })
        ];

        declared.ShouldBe(
            [typeof(ReinstateReservationCommand)],
            "an idempotent command this test does not name has no endpoint held to authentication " +
            "here; name its endpoint below in the change that adds it (§8.5)");

        Endpoint endpoint = Endpoints
            .Where(e => Name(e) == "ReinstateReservation")
            .ShouldHaveSingleItem();

        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(
            "an anonymous reinstatement would claim under the subject every anonymous caller shares (§8.5)");
        endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(a => a.Policy)
            .ShouldContain(InventoryPermissions.Admin);
    }

    private static string? Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
}
