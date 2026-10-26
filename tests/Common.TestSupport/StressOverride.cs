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

    public static IConfiguration Configuration(IReadOnlyList<string> lines) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                lines
                    .Select(line => Entry().Match(line))
                    .Where(entry => entry.Success)
                    .Select(entry => new KeyValuePair<string, string?>(
                        entry.Groups["key"].Value.Replace("__", ":", StringComparison.Ordinal),
                        entry.Groups["value"].Value)))
            .Build();

    [GeneratedRegex("""^\s+(?<key>[A-Za-z]+__[A-Za-z]+):\s*"?(?<value>[^"#\s]+)"?\s*(?:#.*)?$""")]
    private static partial Regex Entry();
}
