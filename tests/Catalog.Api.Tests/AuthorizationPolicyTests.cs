using Catalog.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

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

        // Not vacuous, since the loop below passes over an empty set.
        named.ShouldContain(CatalogPermissions.Write);

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
        // Named by no Catalog endpoint but by the gateway's routes (§10.2), resolved through this provider.
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        (await policies.GetPolicyAsync("authenticated")).ShouldNotBeNull();
    }

    [Fact]
    public void The_listing_is_anonymous_and_the_write_path_is_not()
    {
        // §10.2's catalog-public route is GET-only and names `anonymous`, so the listing is public by design.
        Endpoint listing = Single("GetProducts");
        Endpoint publish = Single("PublishProduct");

        listing.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull();
        publish.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(
            "an AllowAnonymous anywhere on the write path defeats every policy on it");

        publish.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(a => a.Policy)
            .ShouldContain(CatalogPermissions.Write);
    }

    [Fact]
    public void The_one_product_read_is_anonymous_like_the_listing()
    {
        // The same catalog-public route serves it (§10.2), and the group fails closed without this metadata.
        Single("GetProduct").Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull();
    }

    private Endpoint Single(string name) =>
        Endpoints.Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
}
