using System.Diagnostics.Metrics;
using Shipping.Application;
using Shipping.Infrastructure;
using Shipping.Infrastructure.Addresses;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Observability;
using Common.Application;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using AddressRegistration = Shipping.Infrastructure.Addresses.DependencyInjection;
using CarrierRegistration = Shipping.Infrastructure.Carrier.DependencyInjection;

namespace Shipping.Worker.Tests;

/// <summary>§13.6's registration rules, over a <c>ServiceCollection</c> and a <see cref="Meter"/>.</summary>
public class MetricsRegistrationTests
{
    /// <summary>Types deliberately not forced, each with the reason its instrument can go unbuilt.</summary>
    private static readonly Dictionary<Type, string> NotForced = [];

    [Fact]
    public void Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be()
    {
        // The collection, not a built provider, which cannot enumerate its registrations.
        Type[] registered =
        [
            .. BuildServices()
                .Select(d => d.ServiceType)
                .Where(t => t.Name.EndsWith("Metrics", StringComparison.Ordinal))
                .Distinct()
        ];

        HashSet<Type> forced =
        [
            .. typeof(MetricsInitialiser)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(p => p.ParameterType)
        ];

        // Both directions. Unforced-and-unexplained is the drift this exists
        // for; forced-but-unregistered is a host that will not start.
        registered
            .Where(t => !forced.Contains(t) && !NotForced.ContainsKey(t))
            .ShouldBeEmpty("add it to MetricsInitialiser, or to NotForced with a reason");

        forced.ShouldBeSubsetOf(registered);
    }

    /// <summary>The gate-coverage half: the selector holds every metrics type this service registers.</summary>
    [Fact]
    public void The_metrics_selector_actually_selects_something()
    {
        Type[] registered =
        [
            .. BuildServices()
                .Select(d => d.ServiceType)
                .Where(t => t.Name.EndsWith("Metrics", StringComparison.Ordinal))
                .Distinct()
        ];

        registered.ShouldContain(typeof(OutboxMetrics));
        registered.ShouldContain(typeof(MessagingMetrics));
        registered.ShouldContain(typeof(RequestMetrics));
        registered.ShouldContain(typeof(CarrierMetrics));
        registered.ShouldContain(typeof(AddressMetrics));
        registered.ShouldContain(typeof(ShipmentMetrics));
    }

    [Fact]
    public void The_initialiser_is_registered_as_a_hosted_service()
    {
        // ImplementationType rather than a resolve, which would pass if another line had constructed the type.
        BuildServices()
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ShouldContain(typeof(MetricsInitialiser));
    }

    [Fact]
    public void The_outbox_gauges_report_one_measurement_per_lane_on_the_registered_meter()
    {
        StubOutboxStats stats = new();
        List<(string Instrument, double Value, string Lane)> collected = [];

        // The factory has to outlive the collection: a Meter disposed with its
        // factory publishes nothing.
        using IMeterFactory factory = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();

        OutboxMetrics metrics = new(factory, stats, NullLogger<OutboxMetrics>.Instance);
        metrics.ShouldNotBeNull();

        // The same Meter the constructor used, since IMeterFactory caches by name.
        Meter mine = factory.Create(OutboxMetrics.MeterName);

        using MeterListener listener = new();

        // Filter on the meter instance, never its name: a MeterListener is process-wide.
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            collected.Add((instrument.Name, value, LaneOf(tags))));

        listener.Start();
        listener.RecordObservableInstruments();

        // Fails closed: a different instance would enable nothing, and this fails rather than passing vacuously.
        collected.ShouldNotBeEmpty("the listener enabled none of this meter's instruments");

        // The tag is spelled as the Lane column stores it, which §9.4's dispatcher compares against.
        collected.Select(m => m.Instrument).Distinct().ShouldBe(
            ["outbox.oldest.age", "outbox.pending.count", "outbox.abandoned.count"],
            ignoreOrder: true);
        collected.Select(m => m.Lane).Distinct().ShouldBe(["Broker", "Local"], ignoreOrder: true);

        // Every lane the enum declares, since a lane with no gauge has no alert.
        collected
            .Select(m => m.Lane)
            .Distinct()
            .ShouldBe(Enum.GetNames<OutboxLane>(), ignoreOrder: true);

        collected
            .Single(m => m.Instrument == "outbox.oldest.age" && m.Lane == nameof(OutboxLane.Broker))
            .Value.ShouldBe(11);
        collected
            .Single(m => m.Instrument == "outbox.abandoned.count" && m.Lane == nameof(OutboxLane.Local))
            .Value.ShouldBe(62);
    }

    [Fact]
    public void A_foreign_meter_of_the_same_name_is_not_collected()
    {
        // AddMetrics() and AddLogging() because a host adds both and this container is assembled by hand.
        ServiceCollection services = BuildServices();
        services.AddMetrics();
        services.AddLogging();

        using ServiceProvider foreign = services.BuildServiceProvider();

        // Registers gauges named like this test's, on the same meter name, over stats that fail on any read.
        foreign.GetRequiredService<OutboxMetrics>().ShouldNotBeNull();

        using IMeterFactory factory = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();

        OutboxMetrics mineMetrics = new(factory, new StubOutboxStats(), NullLogger<OutboxMetrics>.Instance);
        mineMetrics.ShouldNotBeNull();
        Meter mine = factory.Create(OutboxMetrics.MeterName);

        List<double> collected = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => collected.Add(value));

        listener.Start();

        // The assertion is that this does not throw, and that all six measurements come from the stub.
        Should.NotThrow(() => listener.RecordObservableInstruments());

        collected.Count.ShouldBe(6);
        collected.ShouldAllBe(v => v == 11 || v == 12 || v == 41 || v == 42 || v == 61 || v == 62);
    }

    [Fact]
    public void A_failing_stats_read_yields_no_measurements_rather_than_throwing()
    {
        using IMeterFactory factory = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();

        RecordingLogger logger = new();
        OutboxMetrics metrics = new(factory, new ThrowingOutboxStats(), logger);
        metrics.ShouldNotBeNull();
        Meter mine = factory.Create(OutboxMetrics.MeterName);

        List<double> collected = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => collected.Add(value));

        listener.Start();

        // Enabled, unlike the foreign meter above, since the claim is that recording it is safe.
        Should.NotThrow(() => listener.RecordObservableInstruments());

        collected.ShouldBeEmpty("a failing read must drop the series, not report one");

        // And it must say so, since a permanent failure and a quiet lane are both an absent series.
        // Counted on this thread only, because a host's exporter collects these gauges on its own thread.
        logger.OwnErrors.Count.ShouldBe(3, "one per gauge, carrying the exception");
        logger.OwnErrors.ShouldAllBe(e => e is InvalidOperationException);
    }

    private static string LaneOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == "lane")
                return tag.Value?.ToString() ?? "";
        }

        return "";
    }

    /// <summary>All four registration helpers, over configuration that reaches nothing (§12.4).</summary>
    /// <remarks>All four, as <see cref="MetricsInitialiser"/>'s types are split across them.</remarks>
    private static ServiceCollection BuildServices()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Shipping"] =
                        "Server=shipping-sql.invalid;Database=Shipping;User Id=sa;Password=not-a-real-password",
                    ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@shipping-rabbit.invalid:5672",
                    // Both read eagerly by AddCarrierGateway; HTTPS because the environment below is not Development.
                    [CarrierRegistration.BaseUrlKey] = "https://shipping-carrier.invalid",
                    [CarrierRegistration.ApiKeyKey] = "not-a-real-key",
                    // Read eagerly by AddDeliveryAddressSource, which applies no https rule (§9.7).
                    [AddressRegistration.BaseUrlKey] = "http://shipping-ordering.invalid"
                })
            .Build();

        ServiceCollection services = new();
        services.AddShippingApplication();
        services.AddShippingInfrastructure(configuration);
        services.AddCarrierGateway(configuration, new TestEnvironment());
        services.AddDeliveryAddressSource(configuration);

        return services;
    }

    /// <summary>Production rather than Development, the stricter branch a registration defect is found under.</summary>
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Shipping.Worker.Tests";

        public string EnvironmentName { get; set; } = Environments.Production;

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>Stands in for an unreachable database.</summary>
    private sealed class ThrowingOutboxStats : IOutboxStats
    {
        public double OldestAgeSeconds(OutboxLane lane) => throw new InvalidOperationException("database unreachable");

        public int PendingCount(OutboxLane lane) => throw new InvalidOperationException("database unreachable");

        public int AbandonedCount(OutboxLane lane) => throw new InvalidOperationException("database unreachable");
    }

    /// <summary>Records <c>Error</c> entries only, the level the claim is about.</summary>
    private sealed class RecordingLogger : ILogger<OutboxMetrics>
    {
        private readonly int owner = Environment.CurrentManagedThreadId;
        private readonly List<Exception?> errors = [];

        /// <summary>Errors from the constructing thread, which runs the recorded callbacks.</summary>
        public IReadOnlyList<Exception?> OwnErrors
        {
            get
            {
                lock (this.errors)
                    return [.. this.errors];
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Error)
                return;

            if (Environment.CurrentManagedThreadId != this.owner)
                return;

            lock (this.errors)
                this.errors.Add(exception);
        }
    }

    /// <summary>A distinct number per lane and per question, so no value is read off the wrong call.</summary>
    private sealed class StubOutboxStats : IOutboxStats
    {
        public double OldestAgeSeconds(OutboxLane lane) => lane == OutboxLane.Broker ? 11 : 12;

        public int PendingCount(OutboxLane lane) => lane == OutboxLane.Broker ? 41 : 42;

        public int AbandonedCount(OutboxLane lane) => lane == OutboxLane.Broker ? 61 : 62;
    }
}
