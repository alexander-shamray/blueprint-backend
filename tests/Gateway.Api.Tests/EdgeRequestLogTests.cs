using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>§10.1's request and response log, the one hosting category a host keeps at Information (ADR-159).</summary>
public partial class EdgeRequestLogTests
{
    private const string Hosting = "Microsoft.AspNetCore.Hosting.Diagnostics";

    [Fact]
    public void The_gateway_logs_each_request_and_its_response()
    {
        using GatewayFactory factory = new();

        HostingLogger(factory).IsEnabled(LogLevel.Information).ShouldBeTrue();
    }

    [Fact]
    public void The_stress_override_holds_the_gateway_s_request_log_at_warning()
    {
        // The override's own value, through the real host, so a level the filter misreads fails here too.
        Match entry = OverrideLevel().Match(File.ReadAllText(StressOverride()));
        entry.Success.ShouldBeTrue("the stress override sets no hosting level for the gateway");

        using StressedGatewayFactory factory = new(entry.Groups["level"].Value);

        ILogger logger = HostingLogger(factory);
        logger.IsEnabled(LogLevel.Information).ShouldBeFalse();
        logger.IsEnabled(LogLevel.Warning).ShouldBeTrue();
    }

    private static ILogger HostingLogger(GatewayFactory factory) =>
        factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Hosting);

    // GatewayRateLimitOptionsTests' route to the override, for the reason it gives.
    private static string StressOverride()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return Path.Combine(dir.FullName, "deploy", "compose", "docker-compose.stress.yml");
        }

        throw new InvalidOperationException($"No Platform.slnx above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(
        @"^  gateway:\r?\n(?:(?:    .*|[ \t]*)\r?\n)*?" +
        @"\s+Logging__LogLevel__Microsoft\.AspNetCore\.Hosting:\s*""(?<level>\w+)""",
        RegexOptions.Multiline)]
    private static partial Regex OverrideLevel();

    private sealed class StressedGatewayFactory(string level) : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
            [new("Logging:LogLevel:Microsoft.AspNetCore.Hosting", level)];
    }
}
