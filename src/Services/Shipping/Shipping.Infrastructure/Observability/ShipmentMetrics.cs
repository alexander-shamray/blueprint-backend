using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Observability;

/// <summary>The workers' gauges, waiting by state and overdue by pass (§15.3), on the carrier's meter.</summary>
/// <remarks>On <see cref="CarrierMetrics.MeterName"/>, so §13.2's one <c>AddMeter</c> line covers them.</remarks>
public sealed class ShipmentMetrics
{
    // CA1848 (ADR-019), as OutboxMetrics does.
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

        // Tagged with the enum's name, as the Status column stores it, so a query's spelling matches.
        meter.CreateObservableGauge(
            "shipping.shipments.waiting",
            () => PerState(stats, logger),
            unit: "{shipment}",
            description: "Shipments past their first failed pass, by state.");

        // The row no pass has reached yet, which the gauge above cannot see: an age past a tick is lag.
        meter.CreateObservableGauge(
            "shipping.shipments.overdue",
            () => PerPass(stats, logger),
            unit: "s",
            description: "How long the longest-due shipment each pass would claim has waited for one, by pass.");
    }

    /// <summary>States from the enum, so a new state cannot be left without a gauge.</summary>
    /// <remarks>Contained, since the collector abandons its pass on an exception (§13.6).</remarks>
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
                // Every state is dropped: one missing from a `sum by (state)` reads as a healthy zero, not as no data.
                GaugeReadFailed(logger, exception);
                return [];
            }

            measurements.Add(new Measurement<double>(waiting, Tag(status)));
        }

        return measurements;
    }

    /// <summary>Both passes or neither: one missing from a <c>max by (pass)</c> reads as no wait.</summary>
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
