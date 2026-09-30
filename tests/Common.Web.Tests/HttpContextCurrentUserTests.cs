using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>§11.4's port over a principal, on which the subject rule rests entirely.</summary>
public class HttpContextCurrentUserTests
{
    private static HttpContextCurrentUser For(params Claim[] claims)
    {
        // An identity with an authentication type is authenticated; one without is not.
        DefaultHttpContext context = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"))
        };

        return new HttpContextCurrentUser(new HttpContextAccessor { HttpContext = context });
    }

    [Fact]
    public void Reads_the_subject_from_the_name_identifier_claim()
    {
        Guid subject = Guid.CreateVersion7();

        HttpContextCurrentUser user = For(new Claim(ClaimTypes.NameIdentifier, subject.ToString()));

        user.IsAuthenticated.ShouldBeTrue();
        user.Id.ShouldBe(subject);
    }

    [Fact]
    public void A_display_name_is_not_the_subject()
    {
        // §11.3 sets NameClaimType to preferred_username; only NameIdentifier is a subject.
        HttpContextCurrentUser user = For(new Claim("preferred_username", "ada"));

        Should.Throw<InvalidOperationException>(() => user.Id);
    }

    [Fact]
    public void No_principal_is_anonymous_and_has_no_subject()
    {
        // The message-borne path (§9.4) throws rather than answer Guid.Empty, so a missed guard is loud.
        HttpContextCurrentUser user = new(new HttpContextAccessor());

        user.IsAuthenticated.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => user.Id);
    }

    [Fact]
    public void An_unauthenticated_identity_is_not_a_caller()
    {
        // ASP.NET Core puts a non-null, unauthenticated User on every anonymous request.
        DefaultHttpContext context = new();

        HttpContextCurrentUser user = new(new HttpContextAccessor { HttpContext = context });

        user.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public void An_unauthenticated_identity_carrying_claims_answers_none_of_them()
    {
        // An unauthenticated identity can hold any claims, so no member may read one unasked.
        DefaultHttpContext context = new()
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
                    new Claim(PermissionClaim.Type, "catalog:write")
                ]))
        };

        HttpContextCurrentUser user = new(new HttpContextAccessor { HttpContext = context });

        user.IsAuthenticated.ShouldBeFalse();
        user.HasPermission("catalog:write").ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => user.Id);
    }

    [Fact]
    public void A_second_unauthenticated_identity_contributes_nothing()
    {
        // Identity is the primary one, but FindFirst searches every identity the principal holds.
        Guid subject = Guid.CreateVersion7();

        ClaimsPrincipal principal = new(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, subject.ToString())],
                authenticationType: "Test"));

        principal.AddIdentity(
            new ClaimsIdentity([new Claim(PermissionClaim.Type, "catalog:write")]));

        DefaultHttpContext context = new() { User = principal };

        HttpContextCurrentUser user = new(new HttpContextAccessor { HttpContext = context });

        user.IsAuthenticated.ShouldBeTrue();
        user.Id.ShouldBe(subject);
        user.HasPermission("catalog:write").ShouldBeFalse();
    }

    [Fact]
    public void A_permission_is_the_claim_the_policies_require()
    {
        // The claim type RequirePermission registers, so policy and resource check agree (§11.4).
        HttpContextCurrentUser user = For(
            new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            new Claim(PermissionClaim.Type, "orders:admin"));

        user.HasPermission("orders:admin").ShouldBeTrue();
        user.HasPermission("orders:cancel").ShouldBeFalse();

        // Values are matched whole, never by prefix or as a list.
        user.HasPermission("orders").ShouldBeFalse();
    }

    [Fact]
    public void A_permission_in_another_claim_type_grants_nothing()
    {
        // Only §11.4's claim type is a permission, whatever else the token carries.
        HttpContextCurrentUser user = For(
            new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString()),
            new Claim("roles", "orders:admin"),
            new Claim("scope", "orders:admin"));

        user.HasPermission("orders:admin").ShouldBeFalse();
    }
}
