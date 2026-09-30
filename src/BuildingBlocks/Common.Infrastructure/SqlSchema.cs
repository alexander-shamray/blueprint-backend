using System.Text.RegularExpressions;

namespace Common.Infrastructure;

/// <summary>The one place a schema is checked and delimited, since it is interpolated, never a parameter.</summary>
internal static partial class SqlSchema
{
    /// <summary>The delimited, schema-qualified name of a fixed table in <paramref name="schema"/>.</summary>
    public static string Qualify(string schema, string table, string paramName)
    {
        if (!Identifier().IsMatch(schema))
        {
            throw new ArgumentException(
                $"'{schema}' is not a SQL identifier, and the schema is interpolated " +
                "into this service's messaging statements rather than parameterised.",
                paramName);
        }

        // Delimited, because the pattern admits reserved words; it also refuses `]`, so nothing needs escaping.
        return $"[{schema}].{table}";
    }

    // Bounded at 128, which is what `sysname` holds.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex Identifier();
}
