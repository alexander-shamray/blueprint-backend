using System.Buffers.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Common.Infrastructure.Identity;
using Microsoft.Extensions.Logging;
using Notifications.Application.Contacts;

namespace Notifications.Infrastructure.Contacts;

/// <summary>Refuses a token whose <c>realm-management</c> roles are anything but the grant ADR-052 names.</summary>
/// <remarks>A decorator: the grant is this service's, and <see cref="CachingTokenClient"/> every host's.</remarks>
public sealed partial class GrantCheckedTokenCache(
    ITokenCache inner,
    ContactMetrics metrics,
    ILogger<GrantCheckedTokenCache> log) : ITokenCache
{
    /// <summary>The client Keycloak's admin roles live on, under <c>resource_access</c>.</summary>
    private const string RealmManagement = "realm-management";

    /// <summary><c>view-users</c> and the two query roles it composes on the pinned Keycloak, ordinal-sorted.</summary>
    private static readonly string[] Grant = ["query-groups", "query-users", "view-users"];

    // CA1848 (ADR-019); the messages name neither the token nor the roles (§13.4).
    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "The token this host was issued does not carry exactly its grant on realm-management (ADR-052).")]
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
            // Every cause is the deployment's to fix, so each counts; HttpRequestException passes uncounted.
            metrics.Refused();
            throw new ContactSourceRefusedException(
                "The identity provider did not issue this host a usable token (§11.5).", e);
        }

        string[] granted = [.. Roles(token).Order(StringComparer.Ordinal)];

        if (!granted.SequenceEqual(Grant, StringComparer.Ordinal))
        {
            metrics.Refused();
            GrantIsWrong(log);

            throw new ContactSourceRefusedException(
                $"The realm issued {granted.Length} realm-management role(s) where ADR-052 names {Grant.Length}.");
        }

        return token;
    }

    /// <summary>The roles on <c>realm-management</c>, read and never validated: a signature sizes no grant.</summary>
    /// <remarks>Decoded by hand, as the handler flattens the nested claim to text (ADR-052).</remarks>
    private string[] Roles(string token)
    {
        JwtSecurityToken jwt;

        try
        {
            jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        }
        catch (ArgumentException)
        {
            throw Unreadable();
        }

        try
        {
            using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(jwt.RawPayload));

            return payload.RootElement.TryGetProperty("resource_access", out JsonElement access)
                   && access.ValueKind == JsonValueKind.Object
                   && access.TryGetProperty(RealmManagement, out JsonElement client)
                   && client.ValueKind == JsonValueKind.Object
                   && client.TryGetProperty("roles", out JsonElement roles)
                   && roles.ValueKind == JsonValueKind.Array
                ? [.. roles.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.String ? r.GetString()! : "")]
                : [];
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            throw Unreadable();
        }
    }

    // The parser's exception is dropped, as its message can quote the token (§13.4).
    private ContactSourceRefusedException Unreadable()
    {
        metrics.Refused();
        TokenIsUnreadable(log);

        return new ContactSourceRefusedException("The realm issued this host a token that is not a JWT (ADR-052).");
    }
}
