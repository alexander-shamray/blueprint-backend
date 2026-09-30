using Microsoft.AspNetCore.Authorization;

namespace Common.Web;

/// <summary>The one way a service registers a permission policy, so no host spells the claim type (§11.4).</summary>
public static class AuthorizationPolicyExtensions
{
    /// <summary>Requires an authenticated caller carrying <paramref name="permission"/>.</summary>
    /// <remarks>Authentication is required because <c>RequireClaim</c> alone does not ask for it (§11.4).</remarks>
    public static AuthorizationPolicyBuilder RequirePermission(
        this AuthorizationPolicyBuilder builder,
        string permission) =>
        builder
            .RequireAuthenticatedUser()
            .RequireClaim(PermissionClaim.Type, permission);
}
