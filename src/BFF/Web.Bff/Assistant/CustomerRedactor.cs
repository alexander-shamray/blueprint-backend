using System.Text.RegularExpressions;
using Common.Assistant;

namespace Web.Bff.Assistant;

/// <summary>ADR-151's data policy over the caller's text and each tool result, before the provider sees it.</summary>
/// <remarks>
/// An email address, an international phone number and a payment card number are replaced by a marker. A card is a
/// run of 13 to 19 digits that passes the Luhn check, so an order's amounts and ids are left alone (ADR-151).
/// </remarks>
public sealed partial class CustomerRedactor : IRedactor
{
    public const string Email = "[email]";

    public const string Phone = "[phone]";

    public const string Card = "[card]";

    public string Redact(string text, Caller caller)
    {
        string scrubbed = EmailAddress().Replace(text, Email);
        scrubbed = CardNumber().Replace(scrubbed, match => PassesLuhn(match.Value) ? Card : match.Value);

        return InternationalPhone().Replace(scrubbed, Phone);
    }

    private static bool PassesLuhn(string candidate)
    {
        int sum = 0;
        bool doubled = false;

        for (int i = candidate.Length - 1; i >= 0; i--)
        {
            if (!char.IsAsciiDigit(candidate[i]))
                continue;

            int digit = candidate[i] - '0';

            if (doubled)
                digit = digit * 2 > 9 ? (digit * 2) - 9 : digit * 2;

            sum += digit;
            doubled = !doubled;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailAddress();

    // Digits grouped by single spaces or hyphens, as a card is written; not inside a longer run of word characters.
    [GeneratedRegex(@"(?<![\w-])\d(?:[ -]?\d){12,18}(?![\w-])")]
    private static partial Regex CardNumber();

    // A plus and 7 to 15 digits, as E.164 bounds a number, with up to two separators between digits: "+44 (20) 7946".
    [GeneratedRegex(@"\+\d(?:[ ().-]{0,2}\d){6,14}")]
    private static partial Regex InternationalPhone();
}
