using System.Diagnostics.Metrics;
using Payments.Application;
using Payments.Infrastructure;
using Payments.Infrastructure.Observability;
using Payments.Infrastructure.Provider;
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

namespace Payments.Api.Tests;

/// <summary>
/// §13.6's registration rules, which are the ones <c>ValidateOnBuild</c> cannot
/// check: nothing depends on a metrics class, so a container is perfectly happy
/// without one and the instruments simply never exist.
/// </summary>
/// <remarks>
/// No container: every assertion here is about a <c>ServiceCollection</c> or a
/// <see cref="Meter"/>, so these run in the fast half. The gauges' SQL is
/// proven one file over, against a real engine.
/// </remarks>
public class MetricsRegistrationTests
{
    /// <summary>
    /// Types deliberately not forced, each with the reason it does not need to
    /// be. Empty today, and that is the point: a name lands here only with an
    /// argument for why its instrument can go unbuilt.
    /// </summary>
    private static readonly Dictionary<Type, string> NotForced = [];

    [Fact]
    public void Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be()
    {
        // The collection, not a built provider: IServiceCollection is the
        // input to BuildServiceProvider and not itself a registered service,
        // so registrations cannot be enumerated after the build. BuildServices
        // runs every registration helper, because the metrics types are split
        // across all of them.
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

    /// <summary>
    /// The subject of the test above is what it is looking at, and this is
    /// that assertion. A selector that silently matched nothing would pass
    /// both directions vacuously — the repeated failure CLAUDE.md names — so
    /// the candidate set is asserted to hold every metrics type this service
    /// registers.
    /// </summary>
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
        registered.ShouldContain(typeof(ProviderMetrics));
    }

    [Fact]
    public void The_initialiser_is_registered_as_a_hosted_service()
    {
        // The registration is the whole mechanism: without it the singletons
        // above are lazy, nothing resolves them, and the instruments do not
        // exist. ImplementationType rather than a resolve, because a resolve
        // would also pass if some other line had constructed the type.
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

        // The SAME Meter instance the constructor above used — IMeterFactory
        // caches by name, so this is a handle on it rather than a second meter.
        Meter mine = factory.Create(OutboxMetrics.MeterName);

        using MeterListener listener = new();

        // Filter on the meter instance, never on its name. A MeterListener is
        // process-wide, so a name filter would also enable the gauges of any
        // OutboxMetrics another test built — and those read a real OutboxStats
        // against a container that may be gone. The test one method down
        // reproduces that.
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            collected.Add((instrument.Name, value, LaneOf(tags))));

        listener.Start();
        listener.RecordObservableInstruments();

        // Fails closed: if the handle above were ever a different instance,
        // nothing is enabled, nothing is collected, and the assertions below
        // fail rather than passing vacuously.
        collected.ShouldNotBeEmpty("the listener enabled none of this meter's instruments");

        // Three instruments, two lanes, and the tag spelled the way the Lane
        // column stores it — §9.4's dispatcher compares against "Broker", so a
        // lowercase tag would give one value three spellings across SQL, C# and
        // PromQL and every alert would match no series at all.
        collected.Select(m => m.Instrument).Distinct().ShouldBe(
            ["outbox.oldest.age", "outbox.pending.count", "outbox.abandoned.count"],
            ignoreOrder: true);
        collected.Select(m => m.Lane).Distinct().ShouldBe(["Broker", "Local"], ignoreOrder: true);

        // Every lane the enum declares, not a list written out in the gauge. A
        // lane added and forgotten would be a lane with no gauge and therefore
        // no alert.
        collected
            .Select(m => m.Lane)
            .Distinct()
            .ShouldBe(Enum.GetNames<OutboxLane>(), ignoreOrder: true);

        // The values come from IOutboxStats rather than from anywhere else,
        // per lane and per instrument — six readings, six distinct numbers.
        collected
            .Single(m => m.Instrument == "outbox.oldest.age" && m.Lane == nameof(OutboxLane.Broker))
            .Value.ShouldBe(11);
        collected
            .Single(m => m.Instrument == "outbox.abandoned.count" && m.Lane == nameof(OutboxLane.Local))
            .Value.ShouldBe(62);
    }

    /// <summary>
    /// Two <see cref="OutboxMetrics"/> on one meter name stay isolated: only
    /// the instance a listener enabled is collected.
    /// </summary>
    /// <remarks>
    /// A <see cref="MeterListener"/> is process-wide, so a filter on
    /// <c>Meter.Name</c> would also enable another instance's gauges — here,
    /// one wired to a container whose connection string points nowhere.
    /// </remarks>
    [Fact]
    public void A_foreign_meter_of_the_same_name_is_not_collected()
    {
        // AddMetrics() and AddLogging() because a host adds both, not
        // AddPaymentsInfrastructure: OutboxMetrics takes an IMeterFactory and
        // an ILogger, and this container is assembled by hand.
        ServiceCollection services = BuildServices();
        services.AddMetrics();
        services.AddLogging();

        using ServiceProvider foreign = services.BuildServiceProvider();

        // Constructing it registers three observable gauges named exactly like
        // this test's, on a meter with exactly the same name, wired to a stats
        // type that will fail the moment anything reads it.
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

        // The assertion is that this does not throw. Six measurements, all from
        // the stub: three instruments, two lanes, and nothing from the foreign
        // meter — whose gauges would have gone to SQL Server.
        Should.NotThrow(() => listener.RecordObservableInstruments());

        collected.Count.ShouldBe(6);
        collected.ShouldAllBe(v => v == 11 || v == 12 || v == 41 || v == 42 || v == 61 || v == 62);
    }

    /// <summary>
    /// A failing stats read drops this meter's series and nothing else.
    /// </summary>
    /// <remarks>
    /// The collector abandons the rest of its pass on an exception, so an
    /// unhandled one here would take unrelated telemetry down with these
    /// gauges. <c>OutboxMetrics</c> contains the read for that reason, and
    /// this is the assertion that it does.
    /// </remarks>
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

        // Enabled, unlike the foreign meter one file down — the whole point is
        // that recording it is safe.
        Should.NotThrow(() => listener.RecordObservableInstruments());

        collected.ShouldBeEmpty("a failing read must drop the series, not report one");

        // And it must say so: containment alone makes a permanent failure
        // indistinguishable from a healthy quiet lane, because both are an
        // absent series, and §13.5's readiness proves only that the connection
        // opens.
        //
        // Counted on this thread only. Any host in this assembly registers
        // the meter by name, so its exporter collects these gauges on its own
        // thread and logs here too; RecordObservableInstruments() runs the
        // callbacks synchronously, which is what makes the calling thread
        // exactly this pass.
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

    /// <summary>
    /// All three registration helpers, over configuration that reaches
    /// nothing (§12.4's .invalid convention).
    /// </summary>
    /// <remarks>
    /// <c>AddPaymentProvider</c> is the third, and leaving it out would make
    /// this test agree with a <see cref="MetricsInitialiser"/> that forgot
    /// <see cref="ProviderMetrics"/>: the types are split across all three.
    /// </remarks>
    private static ServiceCollection BuildServices()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Payments"] =
                        "Server=payments-sql.invalid;Database=Payments;User Id=sa;Password=not-a-real-password",
                    ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@payments-rabbit.invalid:5672",
                    // Both read eagerly by AddPaymentProvider, which throws
                    // naming the missing one — the same reason the bus key
                    // above is here, and unreachable on the same convention.
                    // HTTPS because the environment below is not Development,
                    // which is the rule that helper applies. §2: no Redis
                    // keys, because this service registers no Redis connection.
                    ["PaymentProvider:BaseUrl"] = "https://payments-provider.invalid",
                    ["PaymentProvider:ApiKey"] = "not-a-real-key"
                })
            .Build();

        ServiceCollection services = new();
        services.AddPaymentsApplication();
        services.AddPaymentsInfrastructure(configuration);
        services.AddPaymentProvider(configuration, new TestEnvironment());

        return services;
    }

    /// <summary>
    /// A minimal <see cref="IHostEnvironment"/>: <c>AddPaymentProvider</c>
    /// reads only <see cref="IHostEnvironment.EnvironmentName"/>, through
    /// <c>IsDevelopment()</c>.
    /// </summary>
    /// <remarks>
    /// Production rather than Development, because the stricter branch is the
    /// one a registration defect would be found under — and the address above
    /// is HTTPS so that this choice costs nothing here.
    /// </remarks>
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Payments.Api.Tests";

        public string EnvironmentName { get; set; } = Environments.Production;

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>
    /// Stands in for an unreachable database: the shape a <c>SqlException</c>
    /// from a timed-out connect or command arrives in.
    /// </summary>
    private sealed class ThrowingOutboxStats : IOutboxStats
    {
        public double OldestAgeSeconds(OutboxLane lane) => throw new InvalidOperationException("database unreachable");

        public int PendingCount(OutboxLane lane) => throw new InvalidOperationException("database unreachable");

        public int AbandonedCount(OutboxLane lane) => throw new InvalidOperationException("database unreachable");
    }

    /// <summary>
    /// Enough of <see cref="ILogger{TCategoryName}"/> to answer one question:
    /// did the contained failure say anything?
    /// </summary>
    /// <remarks>
    /// Hand-written rather than <c>Microsoft.Extensions.Diagnostics.Testing</c>,
    /// which would be a new package, a pin and a row in Appendix B for one
    /// assertion. Only <c>Error</c> is recorded, because that is the level the
    /// claim is about.
    /// </remarks>
    private sealed class RecordingLogger : ILogger<OutboxMetrics>
    {
        private readonly int owner = Environment.CurrentManagedThreadId;
        private readonly List<Exception?> errors = [];

        /// <summary>
        /// The errors logged by the thread that constructed this logger, which
        /// is the one <c>RecordObservableInstruments()</c> runs the callbacks
        /// on.
        /// </summary>
        /// <remarks>
        /// Any host in this assembly subscribes to this meter by name and logs
        /// here from its own export thread; recording the thread separates
        /// this test's pass from that traffic.
        /// </remarks>
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

    /// <summary>
    /// A distinct number per lane and per question, so an assertion cannot pass
    /// by reading the right value off the wrong call.
    /// </summary>
    private sealed class StubOutboxStats : IOutboxStats
    {
        public double OldestAgeSeconds(OutboxLane lane) => lane == OutboxLane.Broker ? 11 : 12;

        public int PendingCount(OutboxLane lane) => lane == OutboxLane.Broker ? 41 : 42;

        public int AbandonedCount(OutboxLane lane) => lane == OutboxLane.Broker ? 61 : 62;
    }
}
