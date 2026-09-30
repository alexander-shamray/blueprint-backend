using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Addresses;

/// <summary>ADR-052's contact row, beside <c>Shipments</c> so erasure and retention leave the shipment whole.</summary>
/// <remarks>A port, not <c>IUnitOfWork.ExecuteRawAsync</c>, as <see cref="GetAsync"/> returns what it read.</remarks>
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
