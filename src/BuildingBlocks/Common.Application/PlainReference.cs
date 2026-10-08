using System.Text;

namespace Common.Application;

/// <summary>A reference a third party mints and a customer reads, in an alphabet that links nowhere (ADR-084).</summary>
/// <remarks>
/// Letters and digits in any script, with hyphens, underscores and single spaces between them: no scheme, slash, dot
/// or at sign a mail client could make a link of. Here, not in the contract, which §4.3 keeps free of validation.
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
            if (!(Rune.IsLetterOrDigit(rune) || (joiner && previous != default)))
                return false;

            previous = rune;
        }

        return Rune.IsLetterOrDigit(previous);
    }
}
