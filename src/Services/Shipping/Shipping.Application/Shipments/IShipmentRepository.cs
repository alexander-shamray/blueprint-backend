using Shipping.Domain.Shipments;

namespace Shipping.Application.Shipments;

/// <summary>§5.6's repository, with a read per key: a consumer knows the order, a worker the shipment.</summary>
public interface IShipmentRepository
{
    /// <summary>The shipment for one order, tracking events included, or null.</summary>
    Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct);

    Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct);

    void Add(Shipment shipment);
}
