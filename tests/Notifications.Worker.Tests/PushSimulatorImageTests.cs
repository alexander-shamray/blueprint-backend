using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>
/// The simulator Compose runs and the server the adapters' tests load are one engine at one version (§14.1), so a
/// mapping that passes here loads there.
/// </summary>
public sealed partial class PushSimulatorImageTests
{
    [Fact]
    public void The_simulator_image_tag_is_the_WireMock_Net_pin()
    {
        string root = RepositoryRoot.Locate();

        string? pin = XDocument
            .Load(Path.Combine(root, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .SingleOrDefault(e => (string?)e.Attribute("Include") == "WireMock.Net")
            ?.Attribute("Version")?.Value;

        MatchCollection images = SimulatorImage().Matches(Unit(root));

        pin.ShouldNotBeNullOrWhiteSpace();
        images.Count.ShouldBe(1);
        images[0].Groups["tag"].Value.ShouldBe(pin);
    }

    [Fact]
    public void The_simulator_publishes_to_loopback_alone()
    {
        Match block = SimulatorBlock().Match(Unit(RepositoryRoot.Locate()));

        block.Success.ShouldBeTrue();
        block.Groups["ports"].Value.Split(',').ShouldAllBe(p => p.Trim().StartsWith("\"127.0.0.1:"));
    }

    private static string Unit(string root) =>
        File.ReadAllText(Path.Combine(root, "deploy", "compose", "services", "notifications.yml"));

    [GeneratedRegex(@"^\s*image:\s*sheyenrath/wiremock\.net:(?<tag>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex SimulatorImage();

    [GeneratedRegex(@"^  push-simulator:\r?\n(?:    .*\r?\n)*?    ports: \[(?<ports>[^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex SimulatorBlock();
}
