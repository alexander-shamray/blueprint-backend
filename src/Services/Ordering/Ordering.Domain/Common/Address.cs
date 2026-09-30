using Common.Domain;

namespace Ordering.Domain.Common;

/// <summary>Where an order ships: a value object on §5.3's terms.</summary>
/// <remarks>
/// Presence and the shape of an ISO 3166-1 alpha-2 code only, not membership, so <c>ZZ</c> constructs: no postcode
/// format and no <c>RegionInfo</c>, whose answer depends on the image's ICU data (ADR-053).
/// </remarks>
public sealed record Address
{
    public string Line1 { get; }
    public string? Line2 { get; }
    public string City { get; }
    public string PostalCode { get; }
    public string Country { get; }

    private Address(string line1, string? line2, string city, string postalCode, string country)
    {
        Line1 = line1;
        Line2 = line2;
        City = city;
        PostalCode = postalCode;
        Country = country;
    }

    public static Address Of(string line1, string? line2, string city, string postalCode, string country)
    {
        EnsurePresent(line1, nameof(line1));
        EnsurePresent(city, nameof(city));
        EnsurePresent(postalCode, nameof(postalCode));

        // Shape only; the message says what to send, which is the fix a caller sending "KAZ" needs.
        if (country is not { Length: 2 } || !country.All(char.IsAsciiLetter))
            throw new DomainException("Country must be a two-letter code (ISO 3166-1 alpha-2).");

        // An empty Line2 is the absence of one, normalised so every consumer sees one representation.
        return new Address(
            line1.Trim(),
            string.IsNullOrWhiteSpace(line2) ? null : line2.Trim(),
            city.Trim(),
            postalCode.Trim(),
            country.ToUpperInvariant());
    }

    private static void EnsurePresent(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"An address needs a {field}.");
    }
}
