using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

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

        // Not vacuous: an endpoint that lost its RequireAuthorization line would empty the set.
        named.ShouldContain(PaymentsPermissions.Admin);

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
        // Named by no Payments endpoint but by the gateway's routes (§10.2), resolved through this provider.
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        (await policies.GetPolicyAsync("authenticated")).ShouldNotBeNull();
    }

    [Fact]
    public void No_payments_endpoint_is_anonymous()
    {
        // What Payments holds for an order is never public, so no endpoint is anonymous by design.
        string[] names = ["GetPayment"];

        foreach (Endpoint endpoint in Endpoints.Where(e => names.Contains(Name(e))))
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(Name(endpoint));
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)
                .ShouldContain(PaymentsPermissions.Admin);
        }

        // Not vacuous — the loop above passes over an empty set, which is what
        // a renamed endpoint would produce.
        Endpoints.Count(e => names.Contains(Name(e))).ShouldBe(names.Length);
    }

    private static string? Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
}
