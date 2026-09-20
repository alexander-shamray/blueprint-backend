using System.Diagnostics.Metrics;
using Common.Application;
using Microsoft.Extensions.Logging;

namespace Ordering.Infrastructure.Observability;

/// <summary>
/// §13.6's three per-lane outbox gauges. Infrastructure rather than
/// Application because it reads the database, which is also why the
/// instruments are observable rather than pushed (§13.3).
/// </summary>
/// <remarks>
/// <b>Singleton, and eagerly constructed by <see cref="MetricsInitialiser"/>.</b>
/// Observable gauges are callbacks held by the <see cref="Meter"/>: if this
/// object is never built, or is built and dropped, the instrument does not
/// exist and every alert reading it is silent — which on a dashboard is
/// indistinguishable from health.
/// <para>
/// <b>Two gauges over the same rows, because they answer different questions
/// and fail differently.</b> <c>outbox.oldest.age</c> catches a lane that has
/// <em>stopped</em>; <c>outbox.pending.count</c> catches one that is
/// <em>falling behind</em>. A single stuck row pins the age gauge at hours
/// while the count stays at one, and a backlog of ten thousand rows all
/// seconds old leaves the age gauge flat. §13.6's alerts read one each.
/// </para>
/// </remarks>
public sealed class OutboxMetrics
{
    /// <summary>
    /// The contract with §13.2's <c>AddMeter</c>. An instrument on an
    /// unregistered meter is collected by nothing and alerted on in vain, so
    /// this constant and the <c>AddMeter("Ordering.Outbox")</c> line in
    /// <c>ObservabilityExtensions</c> are one claim in two files — asserted by
    /// a test rather than left to agree by inspection.
    /// </summary>
    public const string MeterName = "Ordering.Outbox";

    /// <summary>
    /// The only thing that distinguishes a contained failure from a healthy
    /// quiet lane, because both are an absent series on the graph.
    /// </summary>
    /// <remarks>
    /// <c>LoggerMessage.Define</c> rather than an interpolated call, on the
    /// terms ADR-019 already settled for §6.3's <c>LoggingBehavior</c>: CA1848
    /// is an error here, and this runs on the collector's thread once per
    /// export interval for as long as the failure lasts.
    /// </remarks>
    private static readonly Action<ILogger, Exception?> GaugeReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(GaugeReadFailed)),
            "Outbox gauge read failed. PerLane runs once per gauge, so this "
            + "collection omits only that gauge's lane measurements, absent "
            + "rather than wrong — see OutboxMetrics.");

    public OutboxMetrics(IMeterFactory factory, IOutboxStats stats, ILogger<OutboxMetrics> logger)
    {
        Meter meter = factory.Create(MeterName);

        meter.CreateObservableGauge(
            "outbox.oldest.age",
            () => PerLane(stats.OldestAgeSeconds, logger),
            unit: "s",
            description: "Age of the oldest unprocessed row, per lane.");

        // Depth, per lane. The growth alert needs a count and the age gauge
        // cannot supply one — see the class remarks.
        meter.CreateObservableGauge(
            "outbox.pending.count",
            () => PerLane(lane => stats.PendingCount(lane), logger),
            unit: "{message}",
            description: "Unprocessed rows, per lane.");

        // Also per lane, and this is the one where it matters most: a Broker
        // abandonment means other services never learned something, a Local
        // one means this service's own read model is permanently wrong.
        // Different blast radius, different recovery, and outbox-abandoned.md
        // asks which one first.
        meter.CreateObservableGauge(
            "outbox.abandoned.count",
            () => PerLane(lane => stats.AbandonedCount(lane), logger),
            unit: "{message}",
            description: "Rows past the attempt cap, per lane.");
    }

    /// <summary>
    /// One measurement per lane, read from the enum rather than a list here: a
    /// lane added to <see cref="OutboxLane"/> and forgotten at a call site is a
    /// lane with no gauge and no alert, the silent gap §13.6 spends a callout
    /// on. The read is contained because
    /// <c>MeterListener.RecordObservableInstruments</c> propagates a callback's
    /// exception and abandons the rest of the pass, so a failed lane leaves
    /// this gauge's series absent for the interval rather than stopping
    /// unrelated instruments.
    /// </summary>
    private static List<Measurement<double>> PerLane(Func<OutboxLane, double> read, ILogger logger)
    {
        List<Measurement<double>> measurements = [];

        foreach (OutboxLane lane in Enum.GetValues<OutboxLane>())
        {
            double value;

            try
            {
                value = read(lane);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Every lane is dropped, not just this one: half a reading is
                // worse than none, because a lane missing from a `max by (lane)`
                // reads as a healthy zero rather than as no data.
                GaugeReadFailed(logger, exception);
                return [];
            }

            measurements.Add(new Measurement<double>(value, Tag(lane)));
        }

        return measurements;
    }

    /// <summary>
    /// The tag value is the enum's own name, never a hand-written string. The
    /// <c>Lane</c> column stores <c>lane.ToString()</c> and §9.4's dispatcher
    /// compares against <c>"Broker"</c>, so a lowercase tag here would give one
    /// value three spellings across SQL, C# and PromQL — and an alert querying
    /// the wrong one matches no series and never fires, which looks exactly
    /// like health.
    /// </summary>
    private static KeyValuePair<string, object?> Tag(OutboxLane lane) =>
        new("lane", lane.ToString());
}
