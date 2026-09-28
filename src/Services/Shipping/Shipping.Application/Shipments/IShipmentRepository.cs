using Shipping.Domain.Shipments;

namespace Shipping.Application.Shipments;

/// <summary>
/// §5.6's repository for §3.2's aggregate. Two reads because the two callers
/// hold different keys: a consumer knows the order, a worker's claim returns
/// the shipment.
/// </summary>
public interface IShipmentRepository
{
    /// <summary>The shipment for one order, tracking events included, or null.</summary>
    Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct);

    Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct);

    void Add(Shipment shipment);
}
