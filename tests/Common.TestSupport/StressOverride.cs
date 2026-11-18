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

        foreach (string line in lines.Where(line => ConfigurationKey().IsMatch(line)))
        {
            // Dropped, a misspelt key would leave its option at the default, and a strict bind never sees it.
            Match entry = Entry().Match(line);

            if (!entry.Success)
            {
                throw new InvalidOperationException(
                    $"'{line.Trim()}' in the stress override is not a Section__Key entry, so no bind would read it");
            }

            entries.Add(new(
                entry.Groups["key"].Value.Replace("__", ":", StringComparison.Ordinal),
                entry.Groups["value"].Value));
        }

        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }

    // A capitalised key with a lower-case letter is configuration; an environment variable is all capitals, and
    // Compose's own keys are lower case.
    [GeneratedRegex("""^\s+[A-Z][\w.-]*[a-z][\w.-]*:""")]
    private static partial Regex ConfigurationKey();

    [GeneratedRegex(
        """^\s+(?<key>[A-Za-z][A-Za-z0-9]*(?:__[A-Za-z][A-Za-z0-9.]*)+):\s*"?(?<value>[^"#\s]+)"?\s*(?:#.*)?$""")]
    private static partial Regex Entry();
}
