using System.Diagnostics.Metrics;
using Common.Application;
using Microsoft.Extensions.Logging;

namespace Payments.Infrastructure.Observability;

/// <summary>
/// §13.6's three per-lane outbox gauges. Infrastructure because it reads the
/// database, which is also why they are observable rather than pushed (§13.3).
/// </summary>
/// <remarks>
/// Singleton, eagerly constructed by <see cref="MetricsInitialiser"/>: these
/// are callbacks the <see cref="Meter"/> holds, so an instance never built is
/// an instrument that does not exist. Age catches a lane that has stopped and
/// count one that is falling behind; §13.6's alerts read one each.
/// </remarks>
public sealed class OutboxMetrics
{
    /// <summary>
    /// The contract with §13.2's <c>AddMeter</c>: this name and the one
    /// <c>ObservabilityExtensions</c> registers must be the same string, or
    /// the instruments below are collected by nothing.
    /// </summary>
    public const string MeterName = "Payments.Outbox";

    /// <summary>
    /// The only thing that distinguishes a contained failure from a healthy
    /// quiet lane, because both are an absent series on the graph.
    /// </summary>
    /// <remarks>
    /// <c>LoggerMessage.Define</c> rather than an interpolated call, on the
    /// terms ADR-019 settled for §6.3's <c>LoggingBehavior</c>: CA1848 is an
    /// error here, and this runs on the collector's thread once per export
    /// interval for as long as the failure lasts.
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
    /// One measurement per lane, read from the enum rather than a list written
    /// out here: a lane added and forgotten would have no gauge and no alert.
    /// </summary>
    /// <remarks>
    /// The read is contained because the collector abandons the rest of its
    /// pass on an exception, so one lane could stop unrelated instruments. An
    /// absent series is the right failure for a transient outage; the log is
    /// what tells a permanent one from a healthy quiet lane.
    /// </remarks>
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
