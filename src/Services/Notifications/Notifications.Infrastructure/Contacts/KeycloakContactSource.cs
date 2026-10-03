using System.Net;
using System.Text.Json;
using Notifications.Application.Contacts;

namespace Notifications.Infrastructure.Contacts;

/// <summary>The client half of ADR-052's contact read, over Keycloak's admin API.</summary>
/// <remarks>
/// Binds <c>enabled</c>, <c>email</c> and <c>attributes.locale</c> and nothing else (ADR-052), so a name or
/// another attribute never reaches a type. A transient outcome escapes for the worker's backoff.
/// </remarks>
internal sealed class KeycloakContactSource(HttpClient http, ContactMetrics metrics) : IContactSource
{
    public async Task<ContactLookup> GetAsync(Guid customerId, CancellationToken ct)
    {
        if (customerId == Guid.Empty)
            throw new ArgumentException("The empty id is no customer.", nameof(customerId));

        using HttpRequestMessage message = new(HttpMethod.Get, $"users/{customerId:D}");
        using HttpResponseMessage response = await http.SendAsync(message, ct);

        // No such user; the disabled and the mailbox-less below collapse into the same answer (ADR-052).
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ContactLookup.NoSuchCustomer();

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            metrics.Refused();

            throw new ContactSourceRefusedException(
                $"Keycloak refused this host's token with {(int)response.StatusCode} (ADR-052).");
        }

        // A redirect lands here too: none is followed, so a token that reads every user goes nowhere else.
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Keycloak answered the contact read with {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        using JsonDocument user = await ParseAsync(response, ct);

        return Contact(user.RootElement);
    }

    private static ContactLookup Contact(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Keycloak answered the contact read with no user.");

        if (!user.TryGetProperty("enabled", out JsonElement enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidOperationException("Keycloak answered a user with no boolean enabled.");
        }

        // An account taken back from somebody is most often a disabled one, and Keycloak still answers its mailbox.
        if (!enabled.GetBoolean())
            return new ContactLookup.NoSuchCustomer();

        string? email = Email(user);

        return string.IsNullOrWhiteSpace(email)
            ? new ContactLookup.NoSuchCustomer()
            : new ContactLookup.Found(email, Locale(user));
    }

    // The field, never the value: a mailbox in a message is what a log carries (§13.4).
    private static string? Email(JsonElement user)
    {
        if (!user.TryGetProperty("email", out JsonElement email) || email.ValueKind == JsonValueKind.Null)
            return null;

        if (email.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Keycloak answered a user whose email is not a string.");

        string value = email.GetString()!;

        return value.Length <= ContactLimits.MaxEmailLength
            ? value
            : throw new InvalidOperationException(
                $"Keycloak answered an email longer than {ContactLimits.MaxEmailLength}.");
    }

    // Dropped, never refused, when it is not one tag of the shape: an absent locale is an answer (ADR-052).
    private static string? Locale(JsonElement user) =>
        user.TryGetProperty("attributes", out JsonElement attributes)
        && attributes.ValueKind == JsonValueKind.Object
        && attributes.TryGetProperty("locale", out JsonElement locale)
        && locale.ValueKind == JsonValueKind.Array
        && locale.GetArrayLength() == 1
        && locale[0].ValueKind == JsonValueKind.String
        && LanguageTag.IsOne(locale[0].GetString())
            ? locale[0].GetString()
            : null;

    // The parser's exception is dropped, as its message can quote the body (§13.4).
    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(ct);

            return await JsonDocument.ParseAsync(body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Keycloak answered the contact read with no JSON body.");
        }
    }
}
