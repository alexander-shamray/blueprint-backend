using Common.Application;
using Notifications.Application.Records;

namespace Notifications.Application.Intake;

/// <summary>Checks each value another service wrote before it is stored, and drops one unsafe to render.</summary>
/// <remarks>
/// A kept value is bounded and holds no control, format, line-separator or broken character; a dropped one
/// is absent, never a fault, since a throw would carry another service's bytes to <c>_error</c> (§9.8).
/// </remarks>
public static class InboundValues
{
    /// <summary>Shipping's own width for a tracking number, restated because only contracts cross §4.3.</summary>
    public const int MaxTrackingNumberLength = 64;

    /// <summary>A code as <c>CancelReasons</c> and <c>CancelOrigins</c> spell them, at its column's width.</summary>
    public const int MaxCodeLength = OrderRecordLimits.MaxCodeLength;

    /// <summary>Bounded free text with no control, format, line-separator or broken character, or null.</summary>
    public static string? Text(string? value, int maxLength) =>
        ThirdPartyText.Recordable(value, maxLength) ? value : null;

    /// <summary>A tracking number at Shipping's width, in an alphabet that links nowhere, or null (ADR-084).</summary>
    public static string? TrackingNumber(string? value) =>
        Text(value, MaxTrackingNumberLength) is { } kept && PlainReference.IsWellFormed(kept) ? kept : null;

    /// <summary>A wire code: lower-case ASCII letters, digits and underscores, as the contracts spell one.</summary>
    public static string? Code(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxCodeLength)
            return null;

        foreach (char c in value)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
                return null;
        }

        return value;
    }

    /// <summary>An ISO 4217 code's shape: three capital ASCII letters, and no table of which exist (ADR-053).</summary>
    public static string? Currency(string? value) =>
        value is { Length: 3 } && value.All(char.IsAsciiLetterUpper) ? value : null;
}
