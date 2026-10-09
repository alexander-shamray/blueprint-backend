using System.Globalization;
using System.Text.RegularExpressions;
using Shipping.TestSupport;

namespace Platform.IntegrationTests.Journey;

/// <summary>§13.7's starting targets, read from the chapter that owns them rather than restated here.</summary>
internal static partial class Slo
{
    private const string Heading = "## 13.7 Starting SLOs";

    /// <summary>The broker lane's oldest unprocessed row at p99: how long an event may wait to be published.</summary>
    public static TimeSpan BrokerLaneOutbox { get; } = Target("| Outbox oldest unprocessed, **broker lane**");

    /// <summary>Publish to consumer start at p95: how long a published event may take to arrive.</summary>
    public static TimeSpan EventEndToEnd { get; } = Target("| Event end-to-end p95");

    // The first row of the table to start with the label, so a reworded label fails loudly rather than reading 0.
    private static TimeSpan Target(string label)
    {
        string path = Path.Combine(
            SimulatorMappings.RepositoryRoot(),
            "docs",
            "backend-architecture",
            "13-observability.md");

        string row = File.ReadLines(path)
            .SkipWhile(line => line != Heading)
            .Skip(1)
            .TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal))
            .FirstOrDefault(line => line.StartsWith(label, StringComparison.Ordinal)) ??
            throw new InvalidOperationException($"{path} has no row under '{Heading}' starting '{label}' (§13.7).");

        Match target = Seconds().Match(row);
        if (!target.Success)
            throw new InvalidOperationException($"The §13.7 row '{label}' states no target in seconds: {row}");

        return TimeSpan.FromSeconds(double.Parse(target.Groups["seconds"].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"\|\s*<\s*(?<seconds>\d+(\.\d+)?)\s*s\s*\|")]
    private static partial Regex Seconds();
}
