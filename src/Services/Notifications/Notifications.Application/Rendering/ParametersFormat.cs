using System.Text;
using System.Text.Json;

namespace Notifications.Application.Rendering;

/// <summary>The parameters as a row stores them: a JSON object whose <c>v</c> member is its version.</summary>
/// <remarks>
/// A stored row outlives the code that wrote it, so the reader refuses any version but its own rather than guess, and
/// a new member is a new version beside this one, as §9.2 versions a contract (ADR-053 rule 4).
/// </remarks>
public static class ParametersFormat
{
    /// <summary>The one version this code writes and the only one it reads.</summary>
    public const int Version = 1;

    private const string VersionMember = "v";
    private const string OrderIdMember = "orderId";
    private const string OccurredAtMember = "occurredAt";
    private const string AmountMember = "amount";
    private const string CurrencyMember = "currency";
    private const string TrackingNumberMember = "trackingNumber";
    private const string CancelReasonMember = "cancelReason";

    public static string Write(NotificationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber(VersionMember, Version);
            json.WriteString(OrderIdMember, parameters.OrderId);
            json.WriteString(OccurredAtMember, parameters.OccurredAt);

            // A number, which keeps the decimal's scale through the round trip: 42.10 is read back as 42.10.
            if (parameters.Amount is decimal amount)
                json.WriteNumber(AmountMember, amount);

            WriteIfPresent(json, CurrencyMember, parameters.Currency);
            WriteIfPresent(json, TrackingNumberMember, parameters.TrackingNumber);
            WriteIfPresent(json, CancelReasonMember, parameters.CancelReason);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads a stored value, or throws <see cref="UnreadableParametersException"/> quoting none.</summary>
    public static NotificationParameters Read(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);

        try
        {
            using JsonDocument document = JsonDocument.Parse(stored);
            JsonElement root = document.RootElement;

            int version = VersionOf(root);
            if (version != Version)
            {
                throw new UnreadableParametersException(
                    $"The stored parameters are version {version}; this code reads version {Version} alone.");
            }

            return new NotificationParameters
            {
                OrderId = root.GetProperty(OrderIdMember).GetGuid(),
                OccurredAt = root.GetProperty(OccurredAtMember).GetDateTimeOffset(),
                Amount = root.TryGetProperty(AmountMember, out JsonElement amount) ? amount.GetDecimal() : null,
                Currency = StringOrNull(root, CurrencyMember),
                TrackingNumber = StringOrNull(root, TrackingNumberMember),
                CancelReason = StringOrNull(root, CancelReasonMember),
            };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException
                                      or InvalidOperationException)
        {
            // The inner exception names a position and never the text, so no stored value reaches a log.
            throw new UnreadableParametersException($"The stored parameters are not version {Version}'s shape.", e);
        }
    }

    private static int VersionOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(VersionMember, out JsonElement member) ||
            member.ValueKind != JsonValueKind.Number ||
            !member.TryGetInt32(out int version))
        {
            throw new UnreadableParametersException("The stored parameters carry no version.");
        }

        return version;
    }

    private static void WriteIfPresent(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
            json.WriteString(name, value);
    }

    private static string? StringOrNull(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement member) ? member.GetString() : null;
}
