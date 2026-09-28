using System.IdentityModel.Tokens.Jwt;
using Common.Infrastructure.Identity;
using Microsoft.Extensions.Logging;
using Shipping.Application.Addresses;

namespace Shipping.Infrastructure.Addresses;

/// <summary>
/// The half of ADR-052 the realm gate cannot make: this host holds its own
/// token to the one grant that record names, and refuses a token whose
/// <c>permission</c> set is anything else.
/// </summary>
/// <remarks>
/// A decorator rather than a change to <c>CachingTokenClient</c>: the grant is
/// this service's and that building block is every host's. Keycloak's default
/// roles sit in <c>realm_access</c>, which this claim does not carry (ADR-052).
/// </remarks>
public sealed partial class GrantCheckedTokenCache(
    ITokenCache inner,
    AddressMetrics metrics,
    ILogger<GrantCheckedTokenCache> log) : ITokenCache
{
    /// <summary>
    /// The permission this host's client holds, spelt as a literal because its
    /// owner is <c>OrderingPermissions.DeliveryAddress</c> and §4.3 lets no
    /// assembly cross the boundary to read it. The realm's closed-set
    /// assertion is what ties the two spellings together.
    /// </summary>
    private const string DeliveryAddress = "orders:delivery-address";

    private static readonly string[] Grant = [DeliveryAddress];

    // Compiled once rather than parsed per call; CA1848 is enforced by ADR-019.
    // The messages name neither the token nor the permissions (§13.4).
    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "The token this host was issued does not carry exactly its one grant (ADR-052).")]
    private static partial void GrantIsWrong(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "The token this host was issued is not a JWT, so its grant cannot be read (ADR-052).")]
    private static partial void TokenIsUnreadable(ILogger logger);

    public async Task<string> GetAsync(string scope, CancellationToken ct)
    {
        string token;

        try
        {
            token = await inner.GetAsync(scope, ct);
        }
        catch (InvalidOperationException e)
        {
            // CachingTokenClient throws this for a provider that refused the
            // client and for a token response or discovery document it cannot
            // use; every one is the deployment's to fix, so every one counts.
            // Its transient half is HttpRequestException, which passes through
            // and backs the row off uncounted.
            metrics.Refused();
            throw new AddressSourceRefusedException(
                "The identity provider did not issue this host a usable token (§11.5).", e);
        }

        string[] granted =
        [
            .. Read(token)
                .Claims
                .Where(c => c.Type == "permission")
                .Select(c => c.Value)
                .Order(StringComparer.Ordinal)
        ];

        if (!granted.SequenceEqual(Grant, StringComparer.Ordinal))
        {
            metrics.Refused();
            GrantIsWrong(log);

            throw new AddressSourceRefusedException(
                $"The realm issued this host {granted.Length} permission(s) where ADR-052 names exactly one.");
        }

        return token;
    }

    /// <summary>
    /// Read, never validated: this host did not issue the token and is not its
    /// audience, and a signature says nothing about the size of its own grant.
    /// </summary>
    /// <remarks>
    /// A token that is not a JWT is a realm configured wrongly, which no retry
    /// mends, so it counts as a refusal. The parser's exception is not kept,
    /// because its message can quote the token.
    /// </remarks>
    private JwtSecurityToken Read(string token)
    {
        try
        {
            return new JwtSecurityTokenHandler().ReadJwtToken(token);
        }
        catch (ArgumentException)
        {
            metrics.Refused();
            TokenIsUnreadable(log);

            throw new AddressSourceRefusedException("The realm issued this host a token that is not a JWT (ADR-052).");
        }
    }
}
