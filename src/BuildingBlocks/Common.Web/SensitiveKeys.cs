namespace Common.Web;

/// <summary>The one never-log vocabulary, read by the redactor and the scope provider alike (§13.4).</summary>
/// <remarks>Matching is by substring, so <c>pin</c> is absent because <c>Shipping</c> contains it (§13.4).</remarks>
public static class SensitiveKeys
{
    // Both snake_case spellings are listed, because normalising a key would guess at the separator.
    private static readonly string[] Terms =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "authorization",
        "credential",
        "cookie",
        "apikey",
        "api_key",
        "connectionstring",
        "connection_string",
        "privatekey",
        "private_key",
        "cardnumber",
        "card_number",
        "ssn",
        "nationalid",
        "cvv",
        "otp",
        "sessionid",
        "session_id",
        "accountkey",
        "account_key",
        "signature"
    ];

    /// <summary>The never-log terms, wrapped so a caller cannot cast back to the array and rewrite them.</summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(Terms);

    // A foreach rather than Any, because a lambda capturing key would allocate on every attribute.
    public static bool Matches(string key)
    {
        foreach (string term in Terms)
        {
            if (key.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Whether a value carries a connection-string password or a JWT, whatever its key is called.</summary>
    /// <remarks>Not an entropy test, which would redact the ids an incident is triaged by (§13.4).</remarks>
    public static bool LooksLikeSecret(object? value)
    {
        if (value is not string text || text.Length == 0)
            return false;

        if (Assigns(text, "password") || Assigns(text, "pwd"))
            return true;

        // A header serialised from `{"` encodes to these three characters (§13.4); the compact form has two dots.
        if (!text.StartsWith("eyJ", StringComparison.Ordinal))
            return false;

        int dots = 0;

        foreach (char c in text)
        {
            if (c == '.')
                dots++;
        }

        return dots == 2;
    }

    /// <summary>Whether the key is followed by optional whitespace and <c>=</c>, which ADO.NET tolerates.</summary>
    private static bool Assigns(string text, string key)
    {
        int from = 0;

        while (from <= text.Length - key.Length)
        {
            int at = text.IndexOf(key, from, StringComparison.OrdinalIgnoreCase);

            if (at < 0)
                return false;

            int after = at + key.Length;

            while (after < text.Length && char.IsWhiteSpace(text[after]))
                after++;

            if (after < text.Length && text[after] == '=')
                return true;

            from = at + 1;
        }

        return false;
    }
}
