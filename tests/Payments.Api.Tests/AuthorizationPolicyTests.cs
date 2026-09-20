using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §11.4's callout, executed: "enumerate the endpoint policy names from
/// <c>EndpointDataSource</c> in a test and require each to resolve through
/// <c>IAuthorizationPolicyProvider</c>."
/// </summary>
/// <remarks>
/// A policy name is a reference and nothing checks it.
/// <c>RequireAuthorization(PaymentsPermissions.Admin)</c> takes a string —
/// misspell it, or register the policy in a helper the host never calls, and
/// there is no compiler error, no <c>ValidateOnBuild</c> failure and no
/// startup warning. The endpoint throws <c>InvalidOperationException</c> the
/// first time an operator reads a payment, which is to say in production, on
/// the path that matters.
///
/// This reads the names off the built endpoints rather than from a list
/// beside the registrations, which is the whole point: a list would be a
/// third place to keep in step, and it would agree with itself while
/// disagreeing with the host.
/// </remarks>
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

        // Not vacuous: the assertion below passes trivially over an empty set,
        // and an endpoint file that lost its RequireAuthorization line would
        // produce exactly that.
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
        // Not named by any Payments endpoint — the group requires
        // PaymentsPermissions.Admin outright, and the default policy never
        // enters the picture — so the test above cannot see it. It exists for
        // the gateway's route file (§10.2), whose routes name it, and YARP
        // resolves it through this same provider when it loads the
        // configuration.
        IAuthorizationPolicyProvider policies =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        (await policies.GetPolicyAsync("authenticated")).ShouldNotBeNull();
    }

    [Fact]
    public void No_payments_endpoint_is_anonymous()
    {
        // What Payments holds for an order is never public, so there is no
        // listing to make anonymous by design. An AllowAnonymous here would
        // defeat the one policy every path on this service carries.
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
