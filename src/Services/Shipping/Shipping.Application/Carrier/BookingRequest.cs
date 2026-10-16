using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>One shipment offered to the carrier, keyed so a repeated pass gets the first answer.</summary>
/// <remarks>The order rides beside the shipment so a carrier's log can be read back to the order that caused it.</remarks>
public sealed record BookingRequest(ShipmentId ShipmentId, OrderId OrderId, DeliveryAddress Address)
{
    public string IdempotencyKey => $"book:{ShipmentId.Value}";
}
