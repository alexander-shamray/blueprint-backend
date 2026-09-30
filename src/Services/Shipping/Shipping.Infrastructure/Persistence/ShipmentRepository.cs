using Microsoft.EntityFrameworkCore;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Persistence;

internal sealed class ShipmentRepository(ShippingDbContext db) : IShipmentRepository
{
    // Include, since Shipment.Record deduplicates a page against the loaded events.
    public Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct) =>
        db.Shipments.Include(s => s.TrackingEvents).SingleOrDefaultAsync(s => s.OrderId == orderId, ct);

    public Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct) =>
        db.Shipments.Include(s => s.TrackingEvents).SingleOrDefaultAsync(s => s.Id == id, ct);

    public void Add(Shipment shipment) => db.Shipments.Add(shipment);
}
