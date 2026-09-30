using Shipping.Domain.Shipments;

namespace Shipping.Application.Addresses;

/// <summary>ADR-052's read of the order's delivery address from the service that owns it.</summary>
/// <remarks>A fault throws, and a refused credential throws <see cref="AddressSourceRefusedException"/>.</remarks>
public interface IDeliveryAddressSource
{
    Task<AddressLookup> GetAsync(OrderId orderId, CancellationToken ct);
}
