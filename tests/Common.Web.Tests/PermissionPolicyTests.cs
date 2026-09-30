using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What a policy built by <c>RequirePermission</c> demands (§11.4), authentication included.</summary>
public class PermissionPolicyTests
{
    private const string Permission = "catalog:write";

    private static AuthorizationPolicy Policy()
    {
        ServiceCollection services = new();

        services.AddAuthorizationBuilder()
            .AddPolicy(Permission, policy => policy.RequirePermission(Permission));

        return services.BuildServiceProvider()
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(Permission)
            .GetAwaiter()
            .GetResult()!;
    }

    private static Task<AuthorizationResult> EvaluateAsync(ClaimsPrincipal user)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAuthorization();

        return services.BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, resource: null, Policy());
    }

    [Fact]
    public async Task A_caller_holding_the_permission_is_authorised()
    {
        ClaimsPrincipal user = new(
            new ClaimsIdentity([new Claim(PermissionClaim.Type, Permission)], "Test"));

        (await EvaluateAsync(user)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_caller_holding_another_permission_is_not()
    {
        ClaimsPrincipal user = new(
            new ClaimsIdentity([new Claim(PermissionClaim.Type, "catalog:read")], "Test"));

        (await EvaluateAsync(user)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unauthenticated_principal_carrying_the_claim_is_not()
    {
        // Claims without authentication, which a claim requirement alone would admit.
        ClaimsPrincipal user = new(
            new ClaimsIdentity([new Claim(PermissionClaim.Type, Permission)]));

        user.Identity!.IsAuthenticated.ShouldBeFalse("the premise of this test");
        (await EvaluateAsync(user)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void The_policy_states_both_requirements()
    {
        // Read off the built policy, so authentication is visibly part of the contract.
        AuthorizationPolicy policy = Policy();

        policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().ShouldHaveSingleItem();
        policy.Requirements.OfType<ClaimsAuthorizationRequirement>().ShouldHaveSingleItem();
    }
}
