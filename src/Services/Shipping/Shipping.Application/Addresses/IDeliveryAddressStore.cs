using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Addresses;

/// <summary>
/// The delivery address this service keeps for one order (spec, section 7): a
/// table of its own beside <c>Shipments</c>, so erasure and retention delete a
/// row and leave the shipment's record whole (ADR-052).
/// </summary>
/// <remarks>
/// Raw statements through a port rather than <c>IUnitOfWork.ExecuteRawAsync</c>,
/// because <see cref="GetAsync"/> returns what it read. §11.7's erasure deletes
/// by customer; <see cref="SaveAsync"/> carries the customer for that alone.
/// </remarks>
public interface IDeliveryAddressStore
{
    Task SaveAsync(
        OrderId orderId,
        Guid customerId,
        DeliveryAddress address,
        DateTimeOffset fetchedAt,
        CancellationToken ct);

    Task<DeliveryAddress?> GetAsync(OrderId orderId, CancellationToken ct);
}
