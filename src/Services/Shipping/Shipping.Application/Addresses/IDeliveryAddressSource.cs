using Shipping.Domain.Shipments;

namespace Shipping.Application.Addresses;

/// <summary>
/// ADR-052's read, in this service's vocabulary: the order's delivery address
/// from the service that owns it.
/// </summary>
/// <remarks>
/// Two answers and no third. Anything transient throws and the row backs off;
/// a refused credential throws <see cref="AddressSourceRefusedException"/>,
/// which backs off the same way and is counted, because a revoked grant is a
/// decision somebody took rather than an outage to wait out.
/// </remarks>
public interface IDeliveryAddressSource
{
    Task<AddressLookup> GetAsync(OrderId orderId, CancellationToken ct);
}
