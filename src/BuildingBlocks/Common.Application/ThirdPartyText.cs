using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Common.Application;

/// <summary>Whether a string a third party supplied may be recorded: bounded, and printable (ADR-084).</summary>
/// <remarks>Here, where both adapters and Notifications' <c>InboundValues.Text</c> reach it (ADR-084).</remarks>
public static class ThirdPartyText
{
    public static bool Recordable([NotNullWhen(true)] string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            // Read per code point, so a pair is one character and a lone half reads as Surrogate.
            if (CharUnicodeInfo.GetUnicodeCategory(value, i) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate)
            {
                return false;
            }

            if (char.IsHighSurrogate(value[i]))
                i++;
        }

        return true;
    }
}
