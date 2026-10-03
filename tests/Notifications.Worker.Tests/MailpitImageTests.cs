using System.Text.RegularExpressions;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The sink Compose runs is the one the suite asserts against, and its ports bind loopback alone.</summary>
public sealed partial class MailpitImageTests
{
    private static string Unit() =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Locate(), "deploy", "compose", "services", "notifications.yml"));

    [Fact]
    public void The_compose_sink_is_the_image_the_suite_starts()
    {
        MatchCollection images = MailpitImage().Matches(Unit());

        images.Count.ShouldBe(1);
        images[0].Groups["image"].Value.ShouldBe(Mailpit.Image);
    }

    [Theory]
    [InlineData(Mailpit.SmtpPort)]
    [InlineData(Mailpit.ApiPort)]
    public void Each_of_the_sinks_ports_is_published_on_loopback_and_nowhere_wider(int port)
    {
        string[] mappings =
        [
            .. PortsLine().Matches(Unit())
                .SelectMany(m => m.Groups["ports"].Value.Split(','))
                .Select(p => p.Trim().Trim('"'))
                .Where(p => p.EndsWith($":{port}", StringComparison.Ordinal))
        ];

        // The UI shows every message to whoever reaches it, and SMTP takes anyone's submission (§14.1).
        mappings.ShouldHaveSingleItem().ShouldBe($"127.0.0.1:{port}:{port}");
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>axllent/mailpit:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex MailpitImage();

    [GeneratedRegex(@"^\s*ports:\s*\[(?<ports>[^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex PortsLine();
}
