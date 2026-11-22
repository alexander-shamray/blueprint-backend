using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Common.Domain;

/// <summary>Whether a string is bounded and printable: ADR-084's character rule, held here so an aggregate can read
/// it as well as a validator or an adapter (ADR-216).</summary>
public static class VisibleText
{
    public static bool Holds([NotNullWhen(true)] string? value, int maxLength)
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
