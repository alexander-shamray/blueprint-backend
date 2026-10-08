using System.Globalization;

namespace Common.Infrastructure;

/// <summary>Whether a string a third party supplied may be recorded: bounded, and printable (ADR-084).</summary>
/// <remarks>Notifications' <c>InboundValues.Text</c> spells the same rule on the Application side (ADR-084).</remarks>
public static class ThirdPartyText
{
    public static bool Recordable(string value, int maxLength)
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
