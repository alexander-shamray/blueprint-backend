using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Observability;

/// <summary>
/// Spec section 11's gauge over shipments past their first failed pass, by state, and
/// the wait of the longest-due row each pass would claim, on the carrier's meter.
/// </summary>
/// <remarks>
/// <c>CarrierMetrics.MeterName</c> rather than a string of its own, so §13.2's
/// one <c>AddMeter</c> line covers them. A class of its own because this one
/// reads the database, in <c>OutboxMetrics</c>' shape; a singleton built eagerly
/// by <c>MetricsInitialiser</c>, since the gauge is a callback the meter holds.
/// </remarks>
public sealed class ShipmentMetrics
{
    // LoggerMessage.Define rather than an interpolated call (CA1848), as
    // OutboxMetrics does.
    private static readonly Action<ILogger, Exception?> GaugeReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(GaugeReadFailed)),
            "Waiting-shipment gauge read failed. PerState runs once per collection, so this "
            + "collection omits every state rather than reporting some — absent rather than "
            + "wrong, see ShipmentMetrics.");

    private static readonly Action<ILogger, Exception?> OverdueReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(OverdueReadFailed)),
            "Overdue-shipment gauge read failed. This collection omits both passes rather than "
            + "reporting one, see ShipmentMetrics.");

    public ShipmentMetrics(IMeterFactory factory, IShipmentStats stats, ILogger<ShipmentMetrics> logger)
    {
        Meter meter = factory.Create(CarrierMetrics.MeterName);

        // Rows past their first backoff, by state. The tag value is the enum's
        // own name, never a hand-written string: the Status column stores
        // ToString() and a PromQL query on another spelling matches no series
        // and never fires, which looks exactly like health.
        meter.CreateObservableGauge(
            "shipping.shipments.waiting",
            () => PerState(stats, logger),
            unit: "{shipment}",
            description: "Shipments past their first failed pass, by state.");

        // The row no pass has reached yet, which the gauge above cannot see:
        // healthy, each claim takes a due row within a tick, so an age that
        // climbs past one is the replicas falling behind their passes.
        meter.CreateObservableGauge(
            "shipping.shipments.overdue",
            () => PerPass(stats, logger),
            unit: "s",
            description: "How long the longest-due shipment each pass would claim has waited for one, by pass.");
    }

    /// <summary>
    /// One measurement per state, read from the enum rather than from a list
    /// here: a state added to <see cref="ShipmentStatus"/> and forgotten at a
    /// call site would be a state with no gauge and therefore no alert.
    /// </summary>
    /// <remarks>
    /// The read is contained, because an observable callback that throws does
    /// not fail alone — <c>MeterListener.RecordObservableInstruments</c> drops
    /// the rest of the pass with it, as <c>OutboxMetrics.PerLane</c> argues.
    /// </remarks>
    private static List<Measurement<double>> PerState(IShipmentStats stats, ILogger logger)
    {
        List<Measurement<double>> measurements = [];

        foreach (ShipmentStatus status in Enum.GetValues<ShipmentStatus>())
        {
            int waiting;

            try
            {
                waiting = stats.WaitingCount(status.ToString());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Every state is dropped, not just this one: half a reading is
                // worse than none, because a state missing from a
                // `sum by (state)` reads as a healthy zero rather than no data.
                GaugeReadFailed(logger, exception);
                return [];
            }

            measurements.Add(new Measurement<double>(waiting, Tag(status)));
        }

        return measurements;
    }

    /// <summary>
    /// One measurement per pass, both or neither, for <see cref="PerState"/>'s
    /// reason: a pass missing from a <c>max by (pass)</c> reads as no wait.
    /// </summary>
    private static List<Measurement<double>> PerPass(IShipmentStats stats, ILogger logger)
    {
        try
        {
            return
            [
                new(stats.FulfilmentOverdueSeconds(), new KeyValuePair<string, object?>("pass", "fulfilment")),
                new(stats.TrackingOverdueSeconds(), new KeyValuePair<string, object?>("pass", "tracking")),
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OverdueReadFailed(logger, exception);
            return [];
        }
    }

    private static KeyValuePair<string, object?> Tag(ShipmentStatus status) =>
        new("state", status.ToString());
}
