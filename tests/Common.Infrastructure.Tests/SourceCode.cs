using System.Text.RegularExpressions;

namespace Common.Infrastructure.Tests;

/// <summary>C# source read as code, for the suites that hold a call to a file's text.</summary>
internal static partial class SourceCode
{
    /// <summary><paramref name="source"/> with its comments removed and its literals kept.</summary>
    public static string WithoutComments(string source) =>
        Token().Replace(source, found => found.Groups["literal"].Success ? found.Value : string.Empty);

    /// <summary><paramref name="source"/> with its comments removed and each literal left as an empty one.</summary>
    public static string Code(string source) =>
        Token().Replace(source, found => found.Groups["literal"].Success ? "\"\"" : string.Empty);

    // A literal, raw, verbatim, regular or a character, is matched first, so a `//` or `/*` inside one strips no
    // code; an interpolation hole's own literals are not told apart.
    [GeneratedRegex(
        @"(?<literal>(?<raw>""{3,}).*?\k<raw>|(?:@\$?|\$@)""(?:[^""]|"""")*""|""(?:[^""\\\r\n]|\\.)*""|"
            + @"'(?:[^'\\\r\n]|\\.)+')|//[^\r\n]*|/\*.*?\*/",
        RegexOptions.Singleline)]
    private static partial Regex Token();
}
