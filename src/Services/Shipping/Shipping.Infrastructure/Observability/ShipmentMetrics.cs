using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Observability;

/// <summary>
/// Spec section 11's gauge over shipments past their first failed pass, by state, on the
/// same meter as the carrier's and the address's instruments.
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

    private static KeyValuePair<string, object?> Tag(ShipmentStatus status) =>
        new("state", status.ToString());
}
