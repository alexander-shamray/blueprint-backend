using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>
/// One shipment offered to the carrier. The key is the aggregate's (spec,
/// section 4): the crash that doubles the call is the one between the
/// carrier's answer and the commit, and the next pass repeats the call under
/// the same key and receives the first answer.
/// </summary>
public sealed record BookingRequest(ShipmentId ShipmentId, DeliveryAddress Address)
{
    public string IdempotencyKey => $"book:{ShipmentId.Value}";
}
