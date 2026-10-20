using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Common.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class ObservabilityTests
{
    // The instrumentation's own source name, spelled out rather than shared, as the meter list is.
    private const string EfCoreActivitySource = "OpenTelemetry.Instrumentation.EntityFrameworkCore";

    // §13.2's shared names, copied on purpose: a shared constant would delete a name from the assertion too.
    // A service's own meters are not here; Every_meter_a_service_creates_is_exported_by_its_host reads them.
    private static readonly string[] Required =
    [
        "Commerce.Requests",
        "Commerce.Messaging",
        "MassTransit",
        "Microsoft.Extensions.Caching.Hybrid",
        "StackExchange.Redis"
    ];

    [Fact]
    public void Every_meter_an_alert_reads_from_is_collected()
    {
        List<Metric> exported = [];

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(m => m.AddInMemoryExporter(exported));

        using IHost host = builder.Build();

        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();
        IMeterFactory factory = host.Services.GetRequiredService<IMeterFactory>();

        foreach (string name in Required)
            factory.Create(name).CreateCounter<long>("probe.counter").Add(1);

        provider.ForceFlush();

        // A subset, since the runtime and HTTP instrumentation add .NET meters that are wanted too.
        Required.ShouldBeSubsetOf(exported.Select(m => m.MeterName).Distinct());
    }

    [Fact]
    public void Every_meter_a_service_creates_is_exported_by_its_host()
    {
        // The names come from the services' own source, so a meter added tomorrow is judged without anyone
        // editing this test; the host is named as the service's Api is, which §13.2 reads the service from.
        Dictionary<string, List<string>> byService = ServiceMeterNames();

        byService.ShouldNotBeEmpty("no service source was found, so nothing below was checked");

        foreach ((string service, List<string> names) in byService)
        {
            List<Metric> exported = [];

            HostApplicationBuilder builder = TelemetryHost.Builder(applicationName: $"{service}.Api");
            builder.AddObservability();
            builder.Services
                .AddOpenTelemetry()
                .WithMetrics(m => m.AddInMemoryExporter(exported));

            using IHost host = builder.Build();

            MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();
            IMeterFactory factory = host.Services.GetRequiredService<IMeterFactory>();

            foreach (string name in names)
                factory.Create(name).CreateCounter<long>("probe.counter").Add(1);

            provider.ForceFlush();

            names.ShouldBeSubsetOf(exported.Select(m => m.MeterName).Distinct(), $"{service}'s host");
        }
    }

    [Fact]
    public void A_host_exports_its_own_service_s_meters_and_no_other()
    {
        List<Metric> exported = [];

        HostApplicationBuilder builder = TelemetryHost.Builder(applicationName: "Alpha.Api");
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(m => m.AddInMemoryExporter(exported));

        using IHost host = builder.Build();

        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();
        IMeterFactory factory = host.Services.GetRequiredService<IMeterFactory>();

        factory.Create("Alpha.Outbox").CreateCounter<long>("probe.counter").Add(1);
        factory.Create("Beta.Outbox").CreateCounter<long>("probe.counter").Add(1);
        provider.ForceFlush();

        List<string> meters = exported.Select(m => m.MeterName).Distinct().ToList();
        meters.ShouldContain("Alpha.Outbox");
        meters.ShouldNotContain("Beta.Outbox");
    }

    [Fact]
    public void The_one_instrument_the_repo_actually_has_is_collected()
    {
        // Through the production type, so the meter RequestMetrics uses must be one AddObservability registers.
        List<Metric> exported = [];

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(m => m.AddInMemoryExporter(exported));

        using IHost host = builder.Build();

        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();

        RequestMetrics metrics = new(host.Services.GetRequiredService<IMeterFactory>());
        metrics.Recorded("PlaceOrderCommand", "success", TimeSpan.FromMilliseconds(12));

        provider.ForceFlush();

        // Both halves together: the name alone would pass on an unregistered meter.
        exported.ShouldContain(
            m => m.Name == "request.duration" && m.MeterName == "Commerce.Requests",
            $"exported: {string.Join(", ", exported.Select(m => $"{m.MeterName}/{m.Name}"))}");
    }

    [Fact]
    public void The_OTLP_exporter_is_registered()
    {
        // UseOtlpExporter refuses a second call, so a second one fails only if AddObservability made the
        // first; the refusal fires when the provider is built, so the build sits inside the assertion.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services.AddOpenTelemetry().UseOtlpExporter();

        NotSupportedException thrown = Should.Throw<NotSupportedException>(() =>
        {
            using IHost host = builder.Build();
            host.Services.GetRequiredService<MeterProvider>();
        });

        thrown.Message.ShouldContain("UseOtlpExporter");
    }

    [Fact]
    public void The_resource_names_the_service_its_version_and_its_environment()
    {
        ResourceCapturingExporter exporter = new();

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(m => m.AddReader(new BaseExportingMetricReader(exporter)));

        using IHost host = builder.Build();

        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();
        host.Services
            .GetRequiredService<IMeterFactory>()
            .Create("Commerce.Requests")
            .CreateCounter<long>("probe.counter")
            .Add(1);
        provider.ForceFlush();

        Dictionary<string, object> attributes = exporter.Captured
            .ShouldNotBeNull()
            .Attributes
            .ToDictionary(a => a.Key, a => a.Value);

        attributes["service.name"].ShouldBe(TelemetryHost.ServiceName);
        attributes["service.version"].ShouldBe(BuildInfo.Version);
        attributes["deployment.environment"].ShouldBe(TelemetryHost.EnvironmentName);
    }

    [Fact]
    public void The_resource_carries_the_deployment_track_the_environment_supplies()
    {
        // §15.5's canary tells the tracks apart by this attribute, which the chart sets through the SDK's own
        // OTEL_RESOURCE_ATTRIBUTES rather than any production code (ADR-022).
        ResourceCapturingExporter exporter = new();

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OTEL_RESOURCE_ATTRIBUTES"] = "deployment.track=canary"
            });

        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(m => m.AddReader(new BaseExportingMetricReader(exporter)));

        using IHost host = builder.Build();

        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();
        host.Services
            .GetRequiredService<IMeterFactory>()
            .Create("Commerce.Requests")
            .CreateCounter<long>("probe.counter")
            .Add(1);
        provider.ForceFlush();

        Dictionary<string, object> attributes = exporter.Captured
            .ShouldNotBeNull()
            .Attributes
            .ToDictionary(a => a.Key, a => a.Value);

        // The resource only: copying it onto each series is the collector's half (ADR-022).
        attributes["deployment.track"].ShouldBe("canary");

        // The variable adds to the resource rather than replacing it.
        attributes["service.name"].ShouldBe(TelemetryHost.ServiceName);
        attributes["deployment.environment"].ShouldBe(TelemetryHost.EnvironmentName);
    }

    [Fact]
    public void Health_probes_are_filtered_out_of_traces()
    {
        // On the predicate, because TestServer produces no server spans to filter.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();

        using IHost host = builder.Build();

        AspNetCoreTraceInstrumentationOptions options = host.Services
            .GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);

        options.Filter.ShouldNotBeNull();

        // Probes would otherwise dominate trace volume and storage cost.
        options.Filter(Request("/health/live")).ShouldBeFalse();
        options.Filter(Request("/health/ready")).ShouldBeFalse();
        options.Filter(Request("/health/startup")).ShouldBeFalse();
        options.Filter(Request("/orders")).ShouldBeTrue();
    }

    [Fact]
    public void Ef_core_spans_are_collected()
    {
        // A probe activity on the instrumentation's source, since an EF span needs a real DbContext (§13.2).
        List<Activity> exported = [];

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithTracing(t => t.AddInMemoryExporter(exported));

        using IHost host = builder.Build();

        // Before the activity, since StartActivity returns null on a source nothing listens to.
        TracerProvider provider = host.Services.GetRequiredService<TracerProvider>();

        using ActivitySource source = new(EfCoreActivitySource);
        using Activity? activity = source.StartActivity("probe");
        activity?.Stop();

        provider.ForceFlush();

        // The assertion that bites: without the instrumentation nothing listens to this source.
        activity.ShouldNotBeNull("nothing is listening to the EF Core activity source");
        exported.ShouldContain(a => a.Source.Name == EfCoreActivitySource);
    }

    [Theory]
    [InlineData("Commerce.Outbox")]
    [InlineData("Commerce.Claims")]
    public void Joined_trace_spans_are_collected(string sourceName)
    {
        // OutboxDispatcher's and StagedTrace's sources, spelled out like the meters: without either a trace ends at
        // the table its rows wait in (§9.4).
        List<Activity> exported = [];

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithTracing(t => t.AddInMemoryExporter(exported));

        using IHost host = builder.Build();
        TracerProvider provider = host.Services.GetRequiredService<TracerProvider>();

        using ActivitySource source = new(sourceName);
        using Activity? activity = source.StartActivity("probe");
        activity?.Stop();

        provider.ForceFlush();

        activity.ShouldNotBeNull($"nothing is listening to {sourceName}");
        exported.ShouldContain(a => a.Source.Name == sourceName);
    }

    /// <summary>
    /// Every meter name a service project creates, by the service that owns it: the project directory's first
    /// segment, as §13.2's prefix is the host's. A name outside its service's prefix fails here, because no host
    /// would export it.
    /// </summary>
    private static Dictionary<string, List<string>> ServiceMeterNames()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Platform.slnx")))
            root = root.Parent;

        root.ShouldNotBeNull($"No Platform.slnx above '{AppContext.BaseDirectory}'.");

        // A factory's Create, a Meter's constructor, and the two constants that hold a name (MeterName, and
        // OutboundMeter.Name, which only a file named for a meter may use).
        Regex created = new(
            """\w*[Ff]actory\.Create\("([^"]+)"\)|new Meter\("([^"]+)"|MeterName\s*=\s*"([^"]+)[\x22]""");
        Regex meterConstant = new("""\bconst string Name = "([^"]+)";""");
        Dictionary<string, List<string>> byService = [];

        foreach (string area in new[] { "src/Services", "src/BFF" })
        {
            string areaRoot = Path.Combine(root.FullName, area);

            foreach (string file in Directory.EnumerateFiles(areaRoot, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(areaRoot, file);
                string[] segments = relative.Split(Path.DirectorySeparatorChar);

                // src/Services/<Service>/<Project>/..., src/BFF/<Project>/...
                string project = area == "src/Services" ? segments[1] : segments[0];

                if (segments.Contains("obj") || segments.Contains("bin"))
                    continue;

                string service = project.Split('.')[0];
                string text = File.ReadAllText(file);

                IEnumerable<Match> matches = created.Matches(text);

                if (Path.GetFileName(file).Contains("Meter", StringComparison.Ordinal))
                    matches = matches.Concat(meterConstant.Matches(text));

                foreach (Match match in matches)
                {
                    string name = match.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;

                    name.StartsWith($"{service}.", StringComparison.Ordinal)
                        .ShouldBeTrue($"{file} creates '{name}', which no {service} host exports");

                    if (!byService.TryAdd(service, [name]) && !byService[service].Contains(name))
                        byService[service].Add(name);
                }
            }
        }

        return byService;
    }

    private static DefaultHttpContext Request(string path)
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        return context;
    }

    // ParentProvider.GetResource() is the only public route to the resource a
    // provider was configured with; no exported metric carries it.
    private sealed class ResourceCapturingExporter : BaseExporter<Metric>
    {
        public Resource? Captured { get; private set; }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            Captured = ParentProvider?.GetResource();
            return ExportResult.Success;
        }
    }
}
