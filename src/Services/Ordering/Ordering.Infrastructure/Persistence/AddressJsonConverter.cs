using System.Text.Json;
using System.Text.Json.Serialization;
using Ordering.Domain.Common;

namespace Ordering.Infrastructure.Persistence;

/// <summary>§5.3's <c>Address</c> on the <c>Local</c> lane, carried by <c>OrderConfirmedDomainEvent</c>.</summary>
/// <remarks>Read through <see cref="Address.Of"/>, on <see cref="MoneyJsonConverter"/>'s terms.</remarks>
internal sealed class AddressJsonConverter : JsonConverter<Address>
{
    public override Address Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected an object for {nameof(Address)}, found {reader.TokenType}.");

        string? line1 = null;
        string? line2 = null;
        string? city = null;
        string? postalCode = null;
        string? country = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            string property = reader.GetString()!;
            reader.Read();

            // Ordinal, and an unknown value skipped whole, as in MoneyJsonConverter.
            if (property == nameof(Address.Line1))
                line1 = reader.GetString();
            else if (property == nameof(Address.Line2))
                line2 = reader.GetString();
            else if (property == nameof(Address.City))
                city = reader.GetString();
            else if (property == nameof(Address.PostalCode))
                postalCode = reader.GetString();
            else if (property == nameof(Address.Country))
                country = reader.GetString();
            else
                reader.Skip();
        }

        // Line2 is optional on the domain type, so a payload without it is complete.
        if (line1 is null || city is null || postalCode is null || country is null)
        {
            throw new JsonException(
                $"An {nameof(Address)} payload needs {nameof(Address.Line1)}, {nameof(Address.City)}, " +
                $"{nameof(Address.PostalCode)} and {nameof(Address.Country)}. A row written before a " +
                "rename is the likely cause (§9.4).");
        }

        return Address.Of(line1, line2, city, postalCode, country);
    }

    public override void Write(Utf8JsonWriter writer, Address value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(nameof(Address.Line1), value.Line1);

        // Written even when null, so the payload states the absence (§9.2).
        writer.WriteString(nameof(Address.Line2), value.Line2);
        writer.WriteString(nameof(Address.City), value.City);
        writer.WriteString(nameof(Address.PostalCode), value.PostalCode);
        writer.WriteString(nameof(Address.Country), value.Country);
        writer.WriteEndObject();
    }
}
