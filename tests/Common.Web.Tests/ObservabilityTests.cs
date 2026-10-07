using System.Diagnostics;
using System.Diagnostics.Metrics;
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

    // §13.2's list, copied on purpose: a shared constant would delete a name from the assertion too.
    private static readonly string[] Required =
    [
        "Catalog.Outbox",
        "Ordering.Orders",
        "Ordering.Outbox",
        "Inventory.Reservations",
        "Inventory.Outbox",
        "Payments.Provider",
        "Payments.Outbox",
        "Shipping.Outbound",
        "Shipping.Outbox",
        "Notifications.Outbound",
        "Web.Bff.Projection",
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

    [Fact]
    public void Outbox_delivery_spans_are_collected()
    {
        // OutboxDispatcher's source, spelled out like the meters: without it a trace ends at the outbox (§9.4).
        List<Activity> exported = [];

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithTracing(t => t.AddInMemoryExporter(exported));

        using IHost host = builder.Build();
        TracerProvider provider = host.Services.GetRequiredService<TracerProvider>();

        using ActivitySource source = new("Commerce.Outbox");
        using Activity? activity = source.StartActivity("probe");
        activity?.Stop();

        provider.ForceFlush();

        activity.ShouldNotBeNull("nothing is listening to the outbox's activity source");
        exported.ShouldContain(a => a.Source.Name == "Commerce.Outbox");
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
