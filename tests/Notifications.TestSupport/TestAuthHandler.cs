using System.Security.Claims;
using System.Text.Encodings.Web;
using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Notifications.TestSupport;

/// <summary>§12.4's test scheme: a test states its principal in headers, so §11.4's policies run for real.</summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    // SchemeName, not Scheme: AuthenticationHandler<T> declares a Scheme, and CS0108 fails the build.
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string PermissionsHeader = "X-Test-Permissions";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header means anonymous, not authenticated as nobody.
        if (!Request.Headers.TryGetValue(UserHeader, out StringValues userId))
            return Task.FromResult(AuthenticateResult.NoResult());

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];

        // PermissionClaim.Type, not a literal, so the claim is the one §11.4's policies require.
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
