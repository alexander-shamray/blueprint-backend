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
    // The instrumentation package's own source name — the string AddSource
    // subscribes to, spelled out here rather than shared with production code
    // for the reason the meter list below is.
    private const string EfCoreActivitySource = "OpenTelemetry.Instrumentation.EntityFrameworkCore";

    // The test holds its own copy of §13.2's list on purpose. Sharing a
    // constant with the registration would make this vacuous: deleting a name
    // from one place would delete it from the assertion too, and the whole
    // point is that an unregistered meter is collected by nothing and alerted
    // on in vain.
    private static readonly string[] Required =
    [
        "Ordering.Orders",
        "Ordering.Outbox",
        "Inventory.Reservations",
        "Inventory.Outbox",
        "Payments.Provider",
        "Payments.Outbox",
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

        // Resolve the provider BEFORE creating any instrument. An instrument
        // created first has no listener, is never subscribed to, and the test
        // silently measures nothing while appearing to pass its setup.
        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();
        IMeterFactory factory = host.Services.GetRequiredService<IMeterFactory>();

        foreach (string name in Required)
            factory.Create(name).CreateCounter<long>("probe.counter").Add(1);

        provider.ForceFlush();

        // Asserted as a subset, not exact equality. AddRuntimeInstrumentation
        // and the OTLP exporter's own (failing, localhost) export attempt add
        // "System.Runtime", "System.Net.Http" and "System.Net.NameResolution"
        // to this list too — real .NET diagnostics meters that ARE genuinely
        // wanted in production, verified by removing each instrumentation call
        // in isolation and watching the corresponding meter disappear. An
        // exact match would only pass by dropping telemetry the same PR turns
        // on, which is a worse defect than the one this test guards against.
        Required.ShouldBeSubsetOf(exported.Select(m => m.MeterName).Distinct());
    }

    [Fact]
    public void The_one_instrument_the_repo_actually_has_is_collected()
    {
        // The test above guards the strings above against the registration
        // it cannot see.
        // Both copies are here in the test project; RequestMetrics holds a
        // third, unshared copy of "Commerce.Requests" over in
        // Common.Application. Rename that one and the test above stays green
        // while request.duration — the only real instrument in the repo — is
        // collected by nothing.
        //
        // So this one goes through the production type rather than a probe
        // counter, and closes the seam by construction: it cannot pass unless
        // the meter name RequestMetrics really uses is a name AddObservability
        // really registers.
        List<Metric> exported = [];

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(m => m.AddInMemoryExporter(exported));

        using IHost host = builder.Build();

        // Before constructing RequestMetrics, for the reason spelled out
        // above: an instrument created ahead of the provider has no listener
        // and this test would measure nothing while appearing to pass.
        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();

        RequestMetrics metrics = new(host.Services.GetRequiredService<IMeterFactory>());
        metrics.Recorded("PlaceOrderCommand", "success", TimeSpan.FromMilliseconds(12));

        provider.ForceFlush();

        // Both halves asserted together. The name alone would pass against an
        // instrument on an unregistered meter, and the meter alone is what the
        // test above already covers.
        exported.ShouldContain(
            m => m.Name == "request.duration" && m.MeterName == "Commerce.Requests",
            $"exported: {string.Join(", ", exported.Select(m => $"{m.MeterName}/{m.Name}"))}");
    }

    [Fact]
    public void The_OTLP_exporter_is_registered()
    {
        // OTLP export is the production output path and the deliverable this
        // whole extension exists for, yet every other test here installs an
        // in-memory exporter of its own — so deleting UseOtlpExporter() would
        // leave the meter, resource, trace-filter, redaction and composition
        // tests all green.
        //
        // Asserted through the SDK's own guard rather than by reaching into
        // provider internals: UseOtlpExporter refuses to run twice on one
        // IServiceCollection. A second registration therefore fails only if
        // AddObservability already made the first, and removing that line
        // makes this test fail.
        //
        // The guard fires when the provider is BUILT, not when the second call
        // is made — so the build has to be inside the assertion. An earlier
        // version of this test wrapped only the registration call and failed
        // against correct code.
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
        // §15.5's canary compares the new version against the running one, and
        // this attribute is the only thing that tells them apart.
        //
        // NOT service.version, which is the obvious candidate and does not
        // work: BuildInfo strips the source-revision suffix on purpose — "a
        // value that changes every commit turns one series into thousands" —
        // and nothing in this repository sets an assembly version, so every
        // build reports 1.0.0. A registered name is not a live signal.
        //
        // No production code adds it. OTEL_RESOURCE_ATTRIBUTES is the
        // OpenTelemetry SDK's own mechanism and AddObservability's resource
        // builder already honours it, so the chart sets one environment
        // variable and nothing in Common.Web has to know the word "canary".
        // This test is what establishes that — the alternative was an
        // AddAttributes line asserted against itself.
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

        // THIS PROVES THE ATTRIBUTE REACHES THE RESOURCE, AND NOTHING BEYOND
        // THAT. It used to say "Prometheus renders this as
        // `deployment_track`", which is a claim about the collector rather
        // than about this host: under the standard OTLP-to-Prometheus mapping
        // only service.name, service.namespace and service.instance.id become
        // labels on each series, and every other resource attribute goes to
        // `target_info`. §14.1's collector copies this one onto the datapoint
        // with a `transform` processor and the deployed collector must too
        // (ADR-022) — without which canary.json's queries match nothing, which
        // the rollout reads as an absent series and rolls back.
        //
        // A test can only assert its own half, and saying so is the point:
        // the other half is a requirement on an environment, recorded where a
        // person configuring one will find it.
        attributes["deployment.track"].ShouldBe("canary");

        // And the attributes AddObservability sets itself survive alongside
        // it: the environment variable adds to the resource rather than
        // replacing it, which is the half a test of the new key alone would
        // not have caught.
        attributes["service.name"].ShouldBe(TelemetryHost.ServiceName);
        attributes["deployment.environment"].ShouldBe(TelemetryHost.EnvironmentName);
    }

    [Fact]
    public void Health_probes_are_filtered_out_of_traces()
    {
        // Asserted on the registered predicate, not end to end. TestServer
        // produces no ASP.NET Core server spans at all, so an end-to-end
        // version of this test would pass while filtering nothing — verified
        // by probe before it was written this way.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();

        using IHost host = builder.Build();

        AspNetCoreTraceInstrumentationOptions options = host.Services
            .GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);

        options.Filter.ShouldNotBeNull();

        // At a ten-second probe interval across a dozen pods these would
        // otherwise dominate both trace volume and storage cost.
        options.Filter(Request("/health/live")).ShouldBeFalse();
        options.Filter(Request("/health/ready")).ShouldBeFalse();
        options.Filter(Request("/health/startup")).ShouldBeFalse();
        options.Filter(Request("/orders")).ShouldBeTrue();
    }

    [Fact]
    public void Ef_core_spans_are_collected()
    {
        // §13.2 ships AddEntityFrameworkCoreInstrumentation at PR-08, the PR
        // that gave a service a DbContext. Driven through a probe activity on
        // the instrumentation's own source, exactly as the meter tests above
        // drive a probe counter — an EF span needs a real DbContext and a real
        // query, and Common.Web has neither.
        //
        // Two earlier versions of this test were worthless and are worth
        // naming. Reading SetDbStatementForText off IOptionsMonitor asserted a
        // property this package line does not have; scanning IServiceCollection
        // for an "EntityFrameworkCore" ServiceType found nothing, because with
        // no options lambda the call registers nothing in DI at all — it adds a
        // source to the tracer provider. That version failed identically with
        // the instrumentation present and absent, so its mutation run "passed"
        // while proving nothing.
        List<Activity> exported = [];

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithTracing(t => t.AddInMemoryExporter(exported));

        using IHost host = builder.Build();

        // Before starting the activity, for the reason the meter tests give:
        // a source with no listener yet returns null from StartActivity and
        // the test would measure nothing while appearing to set itself up.
        TracerProvider provider = host.Services.GetRequiredService<TracerProvider>();

        using ActivitySource source = new(EfCoreActivitySource);
        using Activity? activity = source.StartActivity("probe");
        activity?.Stop();

        provider.ForceFlush();

        // The null check is the assertion that bites. AddSource is what puts a
        // listener on this name, so without the instrumentation call
        // StartActivity returns null and nothing is ever exported.
        activity.ShouldNotBeNull("nothing is listening to the EF Core activity source");
        exported.ShouldContain(a => a.Source.Name == EfCoreActivitySource);
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
