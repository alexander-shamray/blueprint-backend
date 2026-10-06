using System.Globalization;
using System.Text.RegularExpressions;

namespace Common.TestSupport;

/// <summary>deploy/compose/README.md's seeded products, the owner §14.3's seeders are held to.</summary>
public static partial class SeededProducts
{
    private const string Heading = "## Seeded products";

    public static IReadOnlyList<SeededProduct> FromComposeReadme() =>
        Parse(File.ReadAllLines(Path.Combine(ComposeImage.RepositoryRoot(), "deploy", "compose", "README.md")));

    /// <summary>The table's rows under <see cref="Heading"/>, in order, stopping at the next heading.</summary>
    public static IReadOnlyList<SeededProduct> Parse(IReadOnlyList<string> lines)
    {
        SeededProduct[] rows =
        [
            .. lines
                .SkipWhile(line => line != Heading)
                .Skip(1)
                .TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal))
                .Select(line => Row().Match(line))
                .Where(row => row.Success)
                .Select(row =>
                    new SeededProduct(
                        Guid.Parse(row.Groups["id"].Value, CultureInfo.InvariantCulture),
                        row.Groups["name"].Value,
                        decimal.Parse(row.Groups["amount"].Value, CultureInfo.InvariantCulture),
                        row.Groups["currency"].Value,
                        int.Parse(row.Groups["onHand"].Value, CultureInfo.InvariantCulture)))
        ];

        // An empty table would hold every seeder to nothing, which every seeder passes.
        return rows.Length > 0
            ? rows
            : throw new InvalidOperationException($"deploy/compose/README.md has no rows under '{Heading}' (§14.3).");
    }

    [GeneratedRegex(
        @"^\| `(?<id>[0-9a-f-]{36})` \| (?<name>[^|]+?) \| (?<amount>\d+\.\d+) (?<currency>[A-Z]{3}) \| (?<onHand>\d+) \|$")]
    private static partial Regex Row();
}

public sealed record SeededProduct(Guid Id, string Name, decimal Amount, string Currency, int OnHand);
