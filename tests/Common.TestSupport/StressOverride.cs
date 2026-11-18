using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Common.TestSupport;

/// <summary>ADR-157's stress override, read as configuration, so a suite judges the values it ships.</summary>
public static partial class StressOverride
{
    /// <summary>The override beside the baseline Compose file.</summary>
    public static string Path { get; } =
        System.IO.Path.Combine(ComposeImage.RepositoryRoot(), "deploy", "compose", "docker-compose.stress.yml");

    /// <summary>Every <c>Section__Key</c> environment entry the override sets, under its configuration key.</summary>
    public static IConfiguration Configuration() => Configuration(File.ReadAllLines(Path));

    /// <summary>The entries <paramref name="lines"/> set, refusing a configuration key it cannot read.</summary>
    public static IConfiguration Configuration(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        List<KeyValuePair<string, string?>> entries = [];

        foreach (Match entry in lines.Select(line => Entry().Match(line)).Where(entry => entry.Success))
        {
            string key = entry.Groups["key"].Value;

            bool named = entry.Groups["value"].Success && Named().IsMatch(key);

            // A key with no value passes the host's own through, which the override never means to read.
            if (!entry.Groups["value"].Success || (!key.Contains("__", StringComparison.Ordinal) && !named))
                continue;

            // Dropped, a misspelt key would leave its option at the default, and a strict bind never sees it.
            if (!SectionKey().IsMatch(key))
            {
                throw new InvalidOperationException(
                    $"'{key}' in the stress override is not a Section__Key entry, so no bind would read it");
            }

            string value = entry.Groups["single"].Success
                ? entry.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal)
                : entry.Groups["value"].Value;
            entries.Add(new(key.Replace("__", ":", StringComparison.Ordinal), value));
        }

        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }

    // A map entry, bare, double- or single-quoted, or a list item's `Key=value`, a closing quote only where one
    // opened. A bare value may hold a space or a quote, so the key on such a line is still judged rather than the line
    // skipped; as in YAML, a quoted value runs to its quote, `''` escapes one inside single quotes (`single` marks
    // it), and only a `#` after a space starts a comment.
    [GeneratedRegex(
        """^\s+(?:-\s*(?:"(?<key>[^\s=#"']+)=(?<value>[^"]*)"|""" +
        """'(?<key>[^\s=#"']+)=(?<single>(?<value>(?:[^']|'')*))'|""" +
        """(?<key>[^\s=#"']+)=(?<value>(?:[^#]|(?<=\S)#)*?))|""" +
        """(?<kq>["']?)(?<key>(?:[^\s:#"']|:(?=[^\s"']))+)\k<kq>\s*:\s*""" +
        """(?:"(?<value>[^"]*)"|'(?<single>(?<value>(?:[^']|'')*))'|(?<value>[^\s"'#](?:[^#]|(?<=\S)#)*?))?)""" +
        """\s*(?:(?<=\s)#.*)?$""")]
    private static partial Regex Entry();

    // Beside a `__` in any case, since configuration is case-blind, a capitalised name with a lower-case letter and a
    // value is meant as a key, a section-less one included; the runtime's own mixed-case variables are not.
    [GeneratedRegex("^(?!DOTNET_|ASPNETCORE_|COMPlus_)[A-Z](?=.*[a-z])")]
    private static partial Regex Named();

    // Sections joined by `__`, each a name or an array index.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*(?:__(?:[A-Za-z][A-Za-z0-9.]*|[0-9]+))+$")]
    private static partial Regex SectionKey();
}
