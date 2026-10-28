using System.Text.RegularExpressions;
using Common.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>The stress override runs this host at its chart's floor, each replica an outbox drain (ADR-158).</summary>
public partial class StressOverrideTests
{
    [Fact]
    public void The_stress_override_runs_the_charts_minimum_replicas_without_a_host_port()
    {
        Match entry = OrderingEntry().Match(File.ReadAllText(StressOverride.Path));
        entry.Success.ShouldBeTrue("the stress override has no ordering-api entry");

        string deploy = Path.GetDirectoryName(Path.GetDirectoryName(StressOverride.Path))!;
        Match floor = MinReplicas().Match(File.ReadAllText(Path.Combine(deploy, "helm", "ordering", "values.yaml")));
        floor.Success.ShouldBeTrue("the ordering chart names no minReplicas");

        Match replicas = Replicas().Match(entry.Value);
        replicas.Success.ShouldBeTrue("the stress override's ordering-api entry sets no replicas");
        replicas.Groups["count"].Value.ShouldBe(floor.Groups["count"].Value);

        // The base file binds one host port, which a second replica cannot take.
        entry.Value.ShouldContain("ports: !reset []");
    }

    [GeneratedRegex(@"^  ordering-api:\r?\n(?:(?:    .*|[ \t]*)\r?\n)*", RegexOptions.Multiline)]
    private static partial Regex OrderingEntry();

    [GeneratedRegex(@"^\s+replicas:\s*(?<count>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex Replicas();

    [GeneratedRegex(@"^\s+minReplicas:\s*(?<count>\d+)", RegexOptions.Multiline)]
    private static partial Regex MinReplicas();
}
