using System.Text.RegularExpressions;

namespace Common.Infrastructure.Tests;

/// <summary>C# source read as code, for the suites that hold a call to a file's text.</summary>
internal static partial class SourceCode
{
    /// <summary><paramref name="source"/> with its comments removed and its literals kept.</summary>
    public static string WithoutComments(string source) =>
        Token().Replace(source, found => found.Groups["literal"].Success ? found.Value : string.Empty);

    /// <summary><paramref name="source"/> without its comments, each literal replaced by an empty string.</summary>
    public static string Code(string source) =>
        Token().Replace(source, found => found.Groups["literal"].Success ? "\"\"" : string.Empty);

    /// <summary>
    /// Each call in <paramref name="code"/> whose text starts with <paramref name="opening"/>, which ends in its
    /// open parenthesis, to its own closing one.
    /// </summary>
    /// <remarks>Read <see cref="Code"/>'s output, where no literal holds a parenthesis.</remarks>
    public static string[] Calls(string code, string opening)
    {
        List<string> calls = [];
        for (int at = code.IndexOf(opening, StringComparison.Ordinal);
             at >= 0;
             at = code.IndexOf(opening, at + 1, StringComparison.Ordinal))
        {
            int depth = 0;
            int end = at + opening.Length - 1;
            for (; end < code.Length; end++)
            {
                depth += code[end] switch { '(' => 1, ')' => -1, _ => 0 };
                if (depth == 0)
                    break;
            }

            if (end == code.Length)
                throw new InvalidOperationException($"No closing parenthesis for the {opening} at {at}.");
            calls.Add(code[at..(end + 1)]);
        }

        return [.. calls];
    }

    // A literal, raw, verbatim, regular or a character, is matched first, so a `//` or `/*` inside one strips no
    // code. An interpolation hole is read as part of its literal: its own literals are not told apart, and under
    // `Code` its code is blanked with it.
    [GeneratedRegex(
        @"(?<literal>(?<raw>""{3,}).*?\k<raw>|(?:@\$?|\$@)""(?:[^""]|"""")*""|""(?:[^""\\\r\n]|\\.)*""|"
            + @"'(?:[^'\\\r\n]|\\.)+')|//[^\r\n]*|/\*.*?\*/",
        RegexOptions.Singleline)]
    private static partial Regex Token();
}
