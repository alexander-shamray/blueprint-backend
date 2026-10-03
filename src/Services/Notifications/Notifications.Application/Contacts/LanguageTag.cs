namespace Notifications.Application.Contacts;

/// <summary>The BCP 47 shape a locale is held to before it is stored; anything else is dropped (ADR-052).</summary>
/// <remarks>
/// A shape and not the registry: a two- or three-letter language, then subtags of one to eight ASCII letters or
/// digits. Off RFC 5646 at the edges both ways, as a tag a deployment ships never meets them (ADR-053).
/// </remarks>
public static class LanguageTag
{
    /// <summary>RFC 5646's recommended buffer for one tag.</summary>
    public const int MaxLength = 35;

    public static bool IsOne(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
            return false;

        string[] subtags = value.Split('-');

        return subtags[0].Length is 2 or 3
            && subtags[0].All(char.IsAsciiLetter)
            && subtags.Skip(1).All(s => s.Length is >= 1 and <= 8 && s.All(char.IsAsciiLetterOrDigit));
    }
}
