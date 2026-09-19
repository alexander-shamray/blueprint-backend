using System.Diagnostics.Metrics;
using Inventory.Application;
using Inventory.Application.Reservations;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Observability;
using Common.Application;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// §13.6's registration rules, which are the ones <c>ValidateOnBuild</c> cannot
/// check: nothing depends on a metrics class, so a container is perfectly happy
/// without one and the instruments simply never exist.
/// </summary>
/// <remarks>
/// No container and no <see cref="IntegrationCollection"/> — every assertion
/// here is about a <c>ServiceCollection</c> or about a <see cref="Meter"/>, so
/// these run in the fast half (<c>Category!=Integration</c>). The gauges' SQL
/// is proven one file over, against a real engine, where it can be.
/// </remarks>
public class MetricsRegistrationTests
{
    /// <summary>
    /// Types deliberately not forced, each with the reason it does not need to
    /// be. Empty today, and that is the point: a name lands here only when
    /// somebody argues it in a pull request.
    /// </summary>
    private static readonly Dictionary<Type, string> NotForced = [];

    [Fact]
    public void Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be()
    {
        // The COLLECTION, not a built provider. IServiceCollection is the input
        // to BuildServiceProvider and is not itself a registered service, so
        // asking a provider for one throws — registrations cannot be enumerated
        // after the build.
        //
        // It runs BOTH helpers, which matters here and nowhere else: the types
        // are split across AddInventoryApplication (RequestMetrics,
        // InventoryMetrics) and AddInventoryInfrastructure (OutboxMetrics,
        // MessagingMetrics). A helper that ran only one half would see a subset
        // and fail against a correct MetricsInitialiser — the test reporting a
        // defect in the thing it is guarding.
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
    /// The subject of the test above is what it is <i>looking at</i>, and this
    /// is that assertion. A selector that silently matched nothing would pass
    /// both directions vacuously — the repeated failure this repository names
    /// in <c>CLAUDE.md</c> — so the candidate set is asserted to be non-empty
    /// and to hold a type registered by <c>AddInventoryApplication</c> and one
    /// by <c>AddInventoryInfrastructure</c>, so a selector scoped to only one
    /// of the two cannot pass unnoticed.
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
        registered.ShouldContain(typeof(InventoryMetrics));
        registered.ShouldContain(typeof(MessagingMetrics));
        registered.ShouldContain(typeof(RequestMetrics));
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

        // Filter on the meter INSTANCE, never on its name. A MeterListener is
        // process-wide and RecordObservableInstruments() invokes every
        // instrument this listener has enabled — so matching `Meter.Name ==
        // "Inventory.Outbox"` also enables the gauges of any OutboxMetrics some
        // other test built, and those are wired to a REAL OutboxStats against a
        // container that may be gone, so its callback throws a SqlException
        // whenever this listener's turn to run falls after that container is
        // torn down. Same shape as Common.Web.Tests' process-wide
        // DiagnosticListener, which is why that project disables parallelism.
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
    /// A process-wide <see cref="MeterListener"/> matched by name enables
    /// another test's gauges, wired to a dead container, rather than only
    /// the ones this test built.
    /// </summary>
    /// <remarks>
    /// A <see cref="MeterListener"/> is process-wide, and
    /// <c>RecordObservableInstruments()</c> invokes every instrument the
    /// listener has enabled — not the ones the test created. A filter on
    /// <c>Meter.Name</c> therefore also enables the gauges of any
    /// <see cref="OutboxMetrics"/> another test built, and those read a REAL
    /// <c>OutboxStats</c> against a database that may be unreachable or gone.
    /// <para>
    /// This reproduces that deterministically: a second <c>OutboxMetrics</c> on
    /// the same meter <i>name</i>, resolved from a container whose connection
    /// string points nowhere. Under a name filter its callbacks run and throw
    /// <c>SqlException</c>; under the instance filter they are never enabled.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_foreign_meter_of_the_same_name_is_not_collected()
    {
        // AddMetrics() and AddLogging() because a HOST adds both, not
        // AddInventoryInfrastructure — OutboxMetrics takes an IMeterFactory and
        // an ILogger, and nothing in the service's own registration supplies
        // either. `WebApplication.CreateBuilder` has always had them; this
        // container is assembled by hand and has to say so.
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
    /// <c>MeterListener.RecordObservableInstruments</c> propagates an exception
    /// out of an observable callback and abandons the rest of the pass, so an
    /// unhandled <c>SqlException</c> here would stop <em>unrelated</em>
    /// instruments being collected — a database outage taking telemetry with it
    /// that has nothing to do with the database. <c>OutboxMetrics</c> contains
    /// the read for that reason, and this is the assertion that it does.
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

        // And it must say so. Containment alone makes a permanent failure —
        // schema drift, a revoked grant — indistinguishable from a healthy
        // quiet lane, because both are an absent series; §13.5's readiness
        // check proves the connection opens and nothing about this table, so
        // it does not report it either. The log is the only signal there is,
        // which makes an unasserted one a signal nobody would miss removing.
        // Counted on THIS thread only, and that is not a detail. A
        // `MeterProvider` built by any host in this assembly registers
        // `AddMeter("Inventory.Outbox")` — by NAME, in ObservabilityExtensions
        // — so it matches the meter created here and collects these very
        // gauges on its own export thread, logging into this logger. Several
        // host-building classes run in parallel with this one, so the count
        // was whatever their timers happened to add.
        //
        // `RecordObservableInstruments()` invokes the callbacks synchronously
        // on the caller, so the calling thread is exactly "our pass" and a
        // foreign collector is exactly "not ours". Filtering on it keeps the
        // assertion exact instead of loosening it to a lower bound.
        //
        // The assembly-wide switch `Common.Web.Tests` carries was the other
        // candidate and is the wrong tool here: its argument is that
        // serialising is cheap *because that suite needs no container*, and
        // this one does. Same hazard, different price, different answer.
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
    /// Both halves of the service's registration, over configuration that
    /// reaches nothing. The .invalid convention of §12.4: no host runs here, so
    /// nothing should be able to dial a real dependency.
    /// </summary>
    private static ServiceCollection BuildServices()
    {
        ServiceCollection services = new();
        services.AddInventoryApplication();
        services.AddInventoryInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Inventory"] =
                        "Server=inventory-sql.invalid;Database=Inventory;User Id=sa;Password=not-a-real-password",
                    ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@inventory-rabbit.invalid:5672",
                    // Both read eagerly by AddRedisConnections, which throws
                    // naming the missing one — the same reason the bus key
                    // above is here, and unreachable on the same convention.
                    ["ConnectionStrings:RedisCache"] = "inventory-redis.invalid:6379",
                    ["ConnectionStrings:RedisCoordination"] = "inventory-redis.invalid:6380"
                })
            .Build());

        return services;
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
        /// is the thread <c>RecordObservableInstruments()</c> runs the gauge
        /// callbacks on.
        /// </summary>
        /// <remarks>
        /// A foreign <c>MeterProvider</c> — any host in this assembly builds
        /// one, and it subscribes to this meter by NAME — collects the same
        /// gauges on its own export thread and logs here too. Recording the
        /// thread is what separates this test's own pass from that traffic,
        /// and it also removes the data race a shared unsynchronised list had.
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
