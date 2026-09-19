using System.Text.RegularExpressions;
using System.Xml.Linq;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// The simulator Compose runs and the server the adapter's tests load are one
/// engine at one version (§14.1), so a mapping that passes here loads there.
/// </summary>
public sealed partial class SimulatorImageTests
{
    [Fact]
    public void The_simulator_image_tag_is_the_WireMock_Net_pin()
    {
        string root = SimulatorMappings.RepositoryRoot();

        string? pin = XDocument.Load(Path.Combine(root, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .SingleOrDefault(e => (string?)e.Attribute("Include") == "WireMock.Net")
            ?.Attribute("Version")?.Value;

        string unit = File.ReadAllText(Path.Combine(root, "deploy", "compose", "services", "payments.yml"));
        MatchCollection images = SimulatorImage().Matches(unit);

        pin.ShouldNotBeNullOrWhiteSpace();
        images.Count.ShouldBe(1);
        images[0].Groups["tag"].Value.ShouldBe(pin);
    }

    [GeneratedRegex(@"^\s*image:\s*sheyenrath/wiremock\.net:(?<tag>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex SimulatorImage();
}
