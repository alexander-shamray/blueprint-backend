using System.IdentityModel.Tokens.Jwt;
using Common.Infrastructure.Identity;
using Microsoft.Extensions.Logging;
using Shipping.Application.Addresses;

namespace Shipping.Infrastructure.Addresses;

/// <summary>Refuses a token whose <c>permission</c> set is anything but the one grant ADR-052 names.</summary>
/// <remarks>A decorator: the grant is this service's, and <see cref="CachingTokenClient"/> every host's.</remarks>
public sealed partial class GrantCheckedTokenCache(
    ITokenCache inner,
    AddressMetrics metrics,
    ILogger<GrantCheckedTokenCache> log) : ITokenCache
{
    /// <summary>A literal, since §4.3 lets no assembly read <c>OrderingPermissions.DeliveryAddress</c>.</summary>
    private const string DeliveryAddress = "orders:delivery-address";

    private static readonly string[] Grant = [DeliveryAddress];

    // CA1848 (ADR-019); the messages name neither the token nor the permissions (§13.4).
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
        catch (InvalidOperationException e) when (e is not ObjectDisposedException)
        {
            // Every cause is the deployment's to fix, so each counts; HttpRequestException and shutdown pass uncounted.
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

    /// <summary>Read, never validated: this host is not the token's audience, and a signature sizes no grant.</summary>
    /// <remarks>The parser's exception is dropped, as its message can quote the token (§13.4).</remarks>
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
