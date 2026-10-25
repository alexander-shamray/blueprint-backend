using System.Text;
using System.Text.RegularExpressions;
using Common.Assistant;

namespace Web.Bff.Assistant;

/// <summary>ADR-151's data policy over the caller's text and each tool result, before the provider sees it.</summary>
/// <remarks>
/// Email addresses, international phone numbers and card numbers become markers. A card is 13 to 19 digits that
/// pass the Luhn check, alone or as whole groups of a longer run, so an order's ids are left alone (ADR-151).
/// </remarks>
public sealed partial class CustomerRedactor : IRedactor
{
    public const string Email = "[email]";

    public const string Phone = "[phone]";

    public const string Card = "[card]";

    public string Redact(string text, Caller caller)
    {
        string scrubbed = EmailAddress().Replace(text, Email);
        scrubbed = CardNumber().Replace(scrubbed, match => RedactCard(match.Value));

        return InternationalPhone().Replace(scrubbed, Phone);
    }

    // A run may hold a card beside an expiry, a security code or another card, so every card in it is replaced, left
    // to right; each group starts at most 19 spans, so a long run costs time in proportion to its length.
    private static string RedactCard(string run)
    {
        MatchCollection groups = DigitGroup().Matches(run);
        StringBuilder redacted = new();
        int copied = 0;
        int first = 0;

        while (first < groups.Count)
        {
            if (LongestCard(run, groups, first) is not { } last)
            {
                first++;

                continue;
            }

            int start = groups[first].Index;
            redacted.Append(run, copied, start - copied).Append(Card);
            copied = groups[last].Index + groups[last].Length;
            first = last + 1;
        }

        return copied == 0 ? run : redacted.Append(run, copied, run.Length - copied).ToString();
    }

    // The last group of the longest span of whole groups from first that holds 13 to 19 digits and passes the check.
    private static int? LongestCard(string run, MatchCollection groups, int first)
    {
        int digits = 0;
        int next = first;

        while (next < groups.Count && digits + groups[next].Length <= 19)
            digits += groups[next++].Length;

        for (int last = next - 1; last >= first && digits >= 13; last--)
        {
            int start = groups[first].Index;

            if (PassesLuhn(run.AsSpan(start, groups[last].Index + groups[last].Length - start)))
                return last;

            digits -= groups[last].Length;
        }

        return null;
    }

    private static bool PassesLuhn(ReadOnlySpan<char> candidate)
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

    // The whole run of grouped digits, unbounded so RedactCard sees groups on both sides of a card; never mid-word.
    [GeneratedRegex(@"(?<![\w-])\d(?:[ -]?\d){12,}(?![\w-])")]
    private static partial Regex CardNumber();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitGroup();

    // A plus and 7 to 15 digits, as E.164 bounds a number, with up to two separators between digits: "+44 (20) 7946".
    [GeneratedRegex(@"\+\d(?:[ ().-]{0,2}\d){6,14}")]
    private static partial Regex InternationalPhone();
}
