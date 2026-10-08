using System.Text;

namespace Common.Contracts.Shipping.V1;

/// <summary>The tracking number's alphabet, held by the contract its minter and its readers share (ADR-084).</summary>
/// <remarks>
/// Letters and digits in any script, with hyphens, underscores and single spaces between them: no scheme, slash, dot
/// or at sign a mail client could make a link of, since the despatch email carries the value word for word (ADR-084).
/// </remarks>
public static class TrackingNumbers
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
