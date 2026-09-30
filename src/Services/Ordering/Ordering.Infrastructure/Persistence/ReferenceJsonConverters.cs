using System.Text.Json;
using System.Text.Json.Serialization;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary><see cref="PaymentReference"/> on the <c>Local</c> lane, as a bare JSON string.</summary>
/// <remarks>Else it deserialises silently to its default, the case <see cref="MoneyJsonConverter"/> closes.</remarks>
internal sealed class PaymentReferenceJsonConverter : JsonConverter<PaymentReference>
{
    public override PaymentReference Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        PaymentReference.Of(
            reader.TokenType == JsonTokenType.String
                ? reader.GetString()!
                : throw new JsonException(
                    $"Expected a string for {nameof(PaymentReference)}, found {reader.TokenType}."));

    public override void Write(Utf8JsonWriter writer, PaymentReference value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary><see cref="TrackingNumber"/> on the <c>Local</c> lane, on the converter above's terms.</summary>
internal sealed class TrackingNumberJsonConverter : JsonConverter<TrackingNumber>
{
    public override TrackingNumber Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        TrackingNumber.Of(
            reader.TokenType == JsonTokenType.String
                ? reader.GetString()!
                : throw new JsonException(
                    $"Expected a string for {nameof(TrackingNumber)}, found {reader.TokenType}."));

    public override void Write(Utf8JsonWriter writer, TrackingNumber value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
