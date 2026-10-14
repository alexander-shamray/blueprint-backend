using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Notifications.Api.Tests;

/// <summary>§12.4's test scheme: a test states its subject in a header, which the service reads.</summary>
/// <remarks>A copy: Notifications.TestSupport references the worker, whose Program collides (§12.4).</remarks>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    // SchemeName, not Scheme: AuthenticationHandler<T> declares a Scheme, and CS0108 fails the build.
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";

    /// <summary>The client the token names in <c>azp</c>, which a preference write records (ADR-120).</summary>
    public const string ClientHeader = "X-Test-Client";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header means anonymous, not authenticated as nobody.
        if (!Request.Headers.TryGetValue(UserHeader, out StringValues userId))
            return Task.FromResult(AuthenticateResult.NoResult());

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];
        if (Request.Headers.TryGetValue(ClientHeader, out StringValues client))
            claims.Add(new Claim("azp", client.ToString()));

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, SchemeName));

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
