using Notifications.Application.Records;

namespace Notifications.Application.Intake;

/// <summary>Checks each value another service wrote before it is stored, and drops one unsafe to render.</summary>
/// <remarks>
/// A kept value is bounded and holds no control, line-separator or bidirectional-formatting character; a dropped one
/// is absent, never a fault, since a throw would carry another service's bytes to <c>_error</c> (§9.8).
/// </remarks>
public static class InboundValues
{
    /// <summary>Shipping's own width for a tracking number, restated because only contracts cross §4.3.</summary>
    public const int MaxTrackingNumberLength = 64;

    /// <summary>A code as <c>CancelReasons</c> and <c>CancelOrigins</c> spell them, at its column's width.</summary>
    public const int MaxCodeLength = OrderRecordLimits.MaxCodeLength;

    /// <summary>Free text, kept when it is bounded and every character renders as itself.</summary>
    public static string? Text(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            return null;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c) || char.IsControl(c) || IsLineBreaking(c) || IsBidiFormatting(c))
                return null;
        }

        return value;
    }

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

    // U+2028 and U+2029, which break a line wherever a renderer honours them.
    private static bool IsLineBreaking(char c) => c is (char)0x2028 or (char)0x2029;

    // Marks, embeddings, overrides and isolates: each changes the order text displays in without being visible.
    private static bool IsBidiFormatting(char c) =>
        c is (char)0x061C or (char)0x200E or (char)0x200F
            or (>= (char)0x202A and <= (char)0x202E)
            or (>= (char)0x2066 and <= (char)0x2069);
}
