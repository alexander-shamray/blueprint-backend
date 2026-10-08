using System.Globalization;
using System.Text;

namespace Common.Application;

/// <summary>A reference a third party mints and a customer reads, in an alphabet that links nowhere.</summary>
/// <remarks>
/// Letters and digits in any script, joined singly by hyphens, underscores or spaces, with no modifier letter, as
/// some are drawn as a dot or a colon (ADR-084). Here, not in the contract, which §4.3 keeps free of validation.
/// </remarks>
public static class PlainReference
{
    public static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        // Per code point, so a letter outside the basic plane is one character and a lone surrogate is none.
        Rune previous = default;
        foreach (Rune rune in value.EnumerateRunes())
        {
            bool joiner = rune.Value is '-' or '_' || (rune.Value == ' ' && previous.Value != ' ');
            if (!(IsCharacter(rune) || (joiner && previous != default)))
                return false;

            previous = rune;
        }

        return IsCharacter(previous);
    }

    private static bool IsCharacter(Rune rune) =>
        Rune.IsLetterOrDigit(rune) && Rune.GetUnicodeCategory(rune) != UnicodeCategory.ModifierLetter;
}
