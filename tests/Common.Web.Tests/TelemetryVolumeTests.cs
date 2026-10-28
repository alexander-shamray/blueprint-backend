using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Common.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What a host sends under load: framework log levels (ADR-159) and trace sampling (ADR-160).</summary>
public partial class TelemetryVolumeTests
{
    private static readonly Action<ILogger, Exception?> InformationProbe =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(InformationProbe)), "probe");

    private static readonly Action<ILogger, Exception?> WarningProbe =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2, nameof(WarningProbe)), "probe");

    // The categories the 2026-10-28 stress run's Loki lines came from, spelled out rather than read from
    // FrameworkCategories, as the meters are: a shared list would delete a category from the assertion too.
    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics")]
    [InlineData("Microsoft.AspNetCore.Routing.EndpointMiddleware")]
    [InlineData("Microsoft.AspNetCore.Http.Result.OkObjectResult")]
    [InlineData("Yarp.ReverseProxy.Forwarder.HttpForwarder")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("System.Net.Http.HttpClient.catalog.LogicalHandler")]
    public void A_framework_category_exports_a_warning_and_not_an_information_line(string category)
    {
        List<LogRecord> exported = Log(TelemetryHost.Builder(), category);

        exported.Select(r => r.LogLevel).ShouldBe([LogLevel.Warning], $"{category} exported");
    }

    // The platform's own lines stay: blueprint-admin's review queue reads Ordering's "flagged for review" line
    // from Loki, and LoggingBehavior's line is the request's own record (§13.3). So do the reasons for a 401 or a
    // 403, which no span carries.
    [Theory]
    [InlineData("Common.Application.LoggingBehavior")]
    [InlineData("Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler")]
    [InlineData("Microsoft.AspNetCore.Authorization.DefaultAuthorizationService")]
    [InlineData("Ordering.Application.Orders.FlagOrderForReview.FlagOrderForReviewHandler")]
    [InlineData("Microsoft.Hosting.Lifetime")]
    public void A_platform_category_still_exports_an_information_line(string category)
    {
        List<LogRecord> exported = Log(TelemetryHost.Builder(), category);

        exported.Select(r => r.LogLevel).ShouldBe([LogLevel.Information, LogLevel.Warning], $"{category} exported");
    }

    [Fact]
    public void A_level_the_configuration_names_for_a_framework_category_wins()
    {
        // An operator lowering one category while debugging, through the Logging section every real host binds.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Logging:LogLevel:Microsoft.AspNetCore.Hosting"] = "Information" });

        List<LogRecord> exported = Log(builder, "Microsoft.AspNetCore.Hosting.Diagnostics");

        exported.Select(r => r.LogLevel).ShouldBe([LogLevel.Information, LogLevel.Warning]);
    }

    [Fact]
    public void The_sampler_the_stress_override_names_is_the_one_the_host_applies()
    {
        // The override's own sampler name, at a ratio of nothing, so a root span is dropped only if the SDK reads
        // the name; a SetSampler call in AddObservability would outrank the variable and keep it.
        string sampler = SamplingEntry("OTEL_TRACES_SAMPLER");
        List<Activity> exported = [];

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OTEL_TRACES_SAMPLER"] = sampler,
                ["OTEL_TRACES_SAMPLER_ARG"] = "0"
            });

        builder.AddObservability();
        builder.Services.AddOpenTelemetry().WithTracing(t => t.AddInMemoryExporter(exported));

        using IHost host = builder.Build();
        TracerProvider provider = host.Services.GetRequiredService<TracerProvider>();

        using ActivitySource source = new("Commerce.Outbox");

        using (Activity? root = source.StartActivity("root"))
            root?.Recorded.ShouldBeFalse();

        // Parent-based: a span whose caller sampled its trace is kept, so a kept trace stays whole across hosts.
        ActivityContext sampledCaller = new(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded,
            isRemote: true);

        using (Activity? child = source.StartActivity("child", ActivityKind.Server, sampledCaller))
            child.ShouldNotBeNull().Recorded.ShouldBeTrue();

        provider.ForceFlush();

        exported.Select(a => a.DisplayName).ShouldBe(["child"]);
    }

    [Fact]
    public void Every_host_that_exports_telemetry_in_Compose_is_sampled_by_the_stress_override()
    {
        // Read from the service files, so a host added tomorrow without the merge fails here rather than tracing
        // in full under the next stress run.
        string compose = Path.GetDirectoryName(StressOverride.Path)!;

        List<string> exporting = Directory
            .EnumerateFiles(Path.Combine(compose, "services"), "*.yml")
            .SelectMany(file => ServiceEntry().Matches(File.ReadAllText(file)))
            .Where(entry => entry.Value.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", StringComparison.Ordinal))
            .Select(entry => entry.Groups["name"].Value)
            .ToList();

        exporting.ShouldNotBeEmpty("no Compose service exports telemetry, so nothing below was checked");

        Dictionary<string, string> overridden = ServiceEntry()
            .Matches(File.ReadAllText(StressOverride.Path))
            .ToDictionary(entry => entry.Groups["name"].Value, entry => entry.Value);

        foreach (string service in exporting)
        {
            overridden.ShouldContainKey(service, $"the stress override has no {service} entry");
            overridden[service].ShouldContain("<<: *trace-sampling", Case.Sensitive, $"{service} is not sampled");
        }

        double ratio = double.Parse(SamplingEntry("OTEL_TRACES_SAMPLER_ARG"), CultureInfo.InvariantCulture);
        ratio.ShouldBeInRange(0.01, 0.5);
    }

    /// <summary>An Information and a Warning line on <paramref name="category"/>, through the host.</summary>
    private static List<LogRecord> Log(HostApplicationBuilder builder, string category)
    {
        List<LogRecord> exported = [];

        // WebApplication.CreateBuilder binds this section before Program.cs runs; the empty builder does not.
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.AddObservability();
        builder.Logging.AddOpenTelemetry(o => o.AddInMemoryExporter(exported));

        using (IHost host = builder.Build())
        {
            ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

            InformationProbe(logger, null);
            WarningProbe(logger, null);
        }

        return exported;
    }

    private static string SamplingEntry(string key)
    {
        Match entry = Regex.Match(
            File.ReadAllText(StressOverride.Path),
            $"""^  {key}:\s*"(?<value>[^"]+)"\s*$""",
            RegexOptions.Multiline);

        entry.Success.ShouldBeTrue($"the stress override's trace-sampling anchor sets no {key}");
        return entry.Groups["value"].Value;
    }

    [GeneratedRegex(@"^  (?<name>[a-z][a-z-]*):\r?\n(?:(?:    .*|[ \t]*)\r?\n)*", RegexOptions.Multiline)]
    private static partial Regex ServiceEntry();
}
