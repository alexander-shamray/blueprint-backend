using System.Text.Json;
using System.Text.Json.Serialization;
using Catalog.Domain.Common;

namespace Catalog.Infrastructure.Persistence;

/// <summary>§5.3's <c>Money</c> on the outbox's <c>Local</c> lane, the JSON twin of its column mapping.</summary>
/// <remarks>
/// Without it a <c>Money</c> deserialises silently to zero and a null currency (§9.4). Reading goes through
/// <see cref="Money.Of"/>, because a stored payload is input too.
/// </remarks>
internal sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected an object for {nameof(Money)}, found {reader.TokenType}.");

        decimal? amount = null;
        string? currency = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            string property = reader.GetString()!;
            reader.Read();

            // Ordinal and case-sensitive, matching the options this converter is registered on (§9.4).
            if (property == nameof(Money.Amount))
                amount = reader.GetDecimal();
            else if (property == nameof(Money.Currency))
                currency = reader.GetString();
            else
                // The whole value, or a nested Amount in a later version's payload (§9.2) is read as this one.
                reader.Skip();
        }

        if (amount is null || currency is null)
        {
            throw new JsonException(
                $"A {nameof(Money)} payload needs both {nameof(Money.Amount)} and " +
                $"{nameof(Money.Currency)}. A row written before a rename is the likely cause (§9.4).");
        }

        return Money.Of(amount.Value, currency);
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber(nameof(Money.Amount), value.Amount);
        writer.WriteString(nameof(Money.Currency), value.Currency);
        writer.WriteEndObject();
    }
}
