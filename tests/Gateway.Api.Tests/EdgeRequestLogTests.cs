using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>§10.1's request and response log, the categories a host keeps at Information by name (ADR-159).</summary>
public class EdgeRequestLogTests
{
    // The lines' own categories, under the configured prefixes: hosting's for every request, and the forwarder's,
    // the ones written inside UseCorrelationId's scope (§10.4), for a proxied one.
    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "Microsoft.AspNetCore.Hosting")]
    [InlineData("Yarp.ReverseProxy.Forwarder.HttpForwarder", "Yarp.ReverseProxy.Forwarder")]
    public void The_gateway_logs_each_request_and_its_response(string category, string configured)
    {
        using GatewayFactory factory = new();

        Logger(factory, category).IsEnabled(LogLevel.Information).ShouldBeTrue($"{configured} is not logged");
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "Microsoft.AspNetCore.Hosting")]
    [InlineData("Yarp.ReverseProxy.Forwarder.HttpForwarder", "Yarp.ReverseProxy.Forwarder")]
    public void The_stress_override_holds_the_gateway_s_request_log_at_warning(string category, string configured)
    {
        // The override's own value, through the real host, so a level the filter misreads fails here too.
        Match entry = OverrideLevel(configured).Match(File.ReadAllText(StressOverride()));
        entry.Success.ShouldBeTrue($"the stress override sets no {configured} level for the gateway");

        using StressedGatewayFactory factory = new(configured, entry.Groups["level"].Value);

        ILogger logger = Logger(factory, category);
        logger.IsEnabled(LogLevel.Information).ShouldBeFalse();
        logger.IsEnabled(LogLevel.Warning).ShouldBeTrue();
    }

    private static ILogger Logger(GatewayFactory factory, string category) =>
        factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

    // Found by walking up, as this suite takes no reference to Common.TestSupport, whose fixtures start containers.
    private static string StressOverride()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return Path.Combine(dir.FullName, "deploy", "compose", "docker-compose.stress.yml");
        }

        throw new InvalidOperationException($"No Platform.slnx above {AppContext.BaseDirectory}.");
    }

    private static Regex OverrideLevel(string configured) =>
        new(
            @"^  gateway:\r?\n(?:(?:    .*|[ \t]*)\r?\n)*?" +
            $@"\s+Logging__LogLevel__{Regex.Escape(configured)}:\s*""(?<level>\w+)""",
            RegexOptions.Multiline);

    private sealed class StressedGatewayFactory(string configured, string level) : GatewayFactory
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
            [new($"Logging:LogLevel:{configured}", level)];
    }
}
