using Common.Application;
using System.Reflection;
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
    public void Every_idempotent_command_reaches_this_service_through_an_authenticated_endpoint()
    {
        // §8.5: an anonymous request's subject is the shared "system" segment, so two anonymous callers reusing
        // one CommandId would collide.
        (Endpoint Endpoint, Type Command)[] idempotent =
        [
            .. Endpoints
                .SelectMany(e => (e.Metadata
                        .GetMetadata<MethodInfo>()?
                        .GetParameters() ?? [])
                    .Where(p => typeof(IIdempotentCommand).IsAssignableFrom(p.ParameterType))
                    .Select(p => (Endpoint: e, Command: p.ParameterType)))
        ];

        // The declared set and the endpoint-bound set must agree, so a command no endpoint binds fails the gate,
        // a broker-only one included (§8.5).
        Type[] declared =
        [
            .. typeof(Catalog.Application.DependencyInjection).Assembly
                .GetTypes()
                .Where(typeof(IIdempotentCommand).IsAssignableFrom)
                .Where(t => t is { IsClass: true, IsAbstract: false })
        ];

        declared.ShouldNotBeEmpty(
            "this service declares an idempotent command; the assembly scan found none");

        idempotent
            .Select(pair => pair.Command)
            .Distinct()
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ShouldBe(
                declared.OrderBy(t => t.Name, StringComparer.Ordinal),
                "every idempotent command must reach this service through an endpoint this test can " +
                "see. A command missing from the left is one no endpoint binds directly — either it " +
                "is broker-only, which §8.5 makes a decision rather than an omission, or an endpoint " +
                "binds a DTO and this selector no longer covers it.");

        foreach ((Endpoint endpoint, Type command) in idempotent)
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(
                $"{endpoint.DisplayName} takes {command.Name} and allows anonymous callers, " +
                "so every one of them claims under the shared system subject (§8.5)");

            endpoint.Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .ShouldNotBeEmpty(
                    $"{endpoint.DisplayName} takes {command.Name} and requires no authorization, " +
                    "so the caller has no subject to key on (§8.5)");
        }
    }

    private Endpoint Single(string name) =>
        Endpoints.Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
}
