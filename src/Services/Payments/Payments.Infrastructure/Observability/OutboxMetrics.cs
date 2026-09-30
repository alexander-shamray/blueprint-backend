using System.Diagnostics.Metrics;
using Common.Application;
using Microsoft.Extensions.Logging;

namespace Payments.Infrastructure.Observability;

/// <summary>§13.6's per-lane outbox gauges, observable because they read the database (§13.3).</summary>
/// <remarks>Built by <see cref="MetricsInitialiser"/>, since an instance never built has no instruments.</remarks>
public sealed class OutboxMetrics
{
    /// <summary>Must be the name §13.2's <c>AddMeter</c> registers, or nothing collects these instruments.</summary>
    public const string MeterName = "Payments.Outbox";

    /// <summary>What tells a contained failure from a quiet lane, both an absent series; CA1848 (ADR-019).</summary>
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

        // The growth alert needs a count, which the age gauge cannot supply (§13.6).
        meter.CreateObservableGauge(
            "outbox.pending.count",
            () => PerLane(lane => stats.PendingCount(lane), logger),
            unit: "{message}",
            description: "Unprocessed rows, per lane.");

        // Per lane matters most here: a Broker abandonment and a Local one differ in blast radius and recovery.
        meter.CreateObservableGauge(
            "outbox.abandoned.count",
            () => PerLane(lane => stats.AbandonedCount(lane), logger),
            unit: "{message}",
            description: "Rows past the attempt cap, per lane.");
    }

    /// <summary>Lanes from the enum, so a new lane cannot be left without a gauge.</summary>
    /// <remarks>Contained, since the collector abandons its pass on an exception (§13.6).</remarks>
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
                // Every lane is dropped: one missing from a `max by (lane)` reads as a healthy zero, not as no data.
                GaugeReadFailed(logger, exception);
                return [];
            }

            measurements.Add(new Measurement<double>(value, Tag(lane)));
        }

        return measurements;
    }

    /// <summary>The enum's name, as the <c>Lane</c> column stores it, so SQL, C# and PromQL agree.</summary>
    private static KeyValuePair<string, object?> Tag(OutboxLane lane) =>
        new("lane", lane.ToString());
}
