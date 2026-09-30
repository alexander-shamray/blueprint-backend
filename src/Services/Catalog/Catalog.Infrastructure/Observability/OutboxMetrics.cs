using System.Diagnostics.Metrics;
using Common.Application;
using Microsoft.Extensions.Logging;

namespace Catalog.Infrastructure.Observability;

/// <summary>§13.6's three per-lane outbox gauges, observable because they read the database (§13.3).</summary>
/// <remarks>
/// Built eagerly by <see cref="MetricsInitialiser"/>. Age catches a stopped lane and count a slow one (§13.6).
/// </remarks>
public sealed class OutboxMetrics
{
    /// <summary>Must equal the name §13.2's <c>AddMeter</c> registers, or nothing collects these gauges.</summary>
    public const string MeterName = "Catalog.Outbox";

    /// <summary>What tells a contained failure from a healthy quiet lane, since both are an absent series.</summary>
    /// <remarks><c>LoggerMessage.Define</c>, because ADR-019 makes CA1848 an error.</remarks>
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

        // Depth, per lane: the growth alert needs a count, which the age gauge cannot supply.
        meter.CreateObservableGauge(
            "outbox.pending.count",
            () => PerLane(lane => stats.PendingCount(lane), logger),
            unit: "{message}",
            description: "Unprocessed rows, per lane.");

        // Per lane most of all: a Broker abandonment leaves other services uninformed, a Local one leaves
        // this service's own read model wrong.
        meter.CreateObservableGauge(
            "outbox.abandoned.count",
            () => PerLane(lane => stats.AbandonedCount(lane), logger),
            unit: "{message}",
            description: "Rows past the attempt cap, per lane.");
    }

    /// <summary>One measurement per lane, read from the enum so that no lane can lack a gauge.</summary>
    /// <remarks>
    /// Contained, because the collector abandons its pass on an exception; <see cref="GaugeReadFailed"/> reports it.
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
                // Every lane dropped, not just this one: a lane missing from `max by (lane)` reads as a healthy zero.
                GaugeReadFailed(logger, exception);
                return [];
            }

            measurements.Add(new Measurement<double>(value, Tag(lane)));
        }

        return measurements;
    }

    /// <summary>The enum's own name, the spelling the <c>Lane</c> column and §9.4's dispatcher use too.</summary>
    private static KeyValuePair<string, object?> Tag(OutboxLane lane) =>
        new("lane", lane.ToString());
}
