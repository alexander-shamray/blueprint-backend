namespace Shipping.Infrastructure.Observability;

/// <summary>The questions <see cref="ShipmentMetrics"/>' gauges ask of <c>shipping.Shipments</c>.</summary>
public interface IShipmentStats
{
    int WaitingCount(string state);

    /// <summary>Zero when the fulfilment claim would take nothing now.</summary>
    double FulfilmentOverdueSeconds();

    /// <summary>Zero when the tracking claim would take nothing now.</summary>
    double TrackingOverdueSeconds();

    /// <summary>Booked rows past <c>ShipmentStats.FirstScanAge</c> that the carrier has never scanned.</summary>
    int UnscannedCount();
}
