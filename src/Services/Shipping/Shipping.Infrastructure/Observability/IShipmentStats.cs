namespace Shipping.Infrastructure.Observability;

/// <summary>
/// The questions spec section 11's gauges ask of <c>shipping.Shipments</c>:
/// how many shipments in each state are past their first failed pass, and how
/// long the longest-due row each pass would claim has waited for one.
/// </summary>
public interface IShipmentStats
{
    int WaitingCount(string state);

    /// <summary>Zero when the fulfilment claim would take nothing now.</summary>
    double FulfilmentOverdueSeconds();

    /// <summary>Zero when the tracking claim would take nothing now.</summary>
    double TrackingOverdueSeconds();
}
