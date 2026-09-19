using System.Security.Claims;
using System.Text.Encodings.Web;
using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Payments.TestSupport;

/// <summary>
/// §12.4's test scheme. Tests state who they are in headers, so
/// authorization runs against a real principal rather than being switched
/// off — the policies of §11.4 are exercised, not bypassed. Installed by
/// <see cref="PaymentsApiFactory.ConfigureAuthentication"/>, which a test
/// may override off — a host still carrying the production scheme is the
/// only one that can prove these headers mean nothing.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    // SchemeName, not Scheme. AuthenticationHandler<T> already declares a
    // protected Scheme — the AuthenticationScheme this handler was resolved
    // for — so the constant §12.4 prints hides it, and CS0108 is an error
    // under ADR-019's TreatWarningsAsErrors. The chapter was amended.
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string PermissionsHeader = "X-Test-Permissions";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header means anonymous, not "authenticated as nobody" — otherwise
        // every 401 test silently passes.
        if (!Request.Headers.TryGetValue(UserHeader, out StringValues userId))
            return Task.FromResult(AuthenticateResult.NoResult());

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];

        // PermissionClaim.Type, not the literal: the same claim §11.4's
        // policies require, read from the assembly that defines it. A test that
        // grants itself payments:write is exercising the policy, and a test
        // scheme spelling its own claim name would grant nothing while looking
        // like it granted everything.
        if (Request.Headers.TryGetValue(PermissionsHeader, out StringValues granted))
        {
            claims.AddRange(
                granted
                    .ToString()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => new Claim(PermissionClaim.Type, p)));
        }

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, SchemeName));

        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
