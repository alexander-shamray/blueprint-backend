using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>One shipment offered to the carrier, keyed so a repeated pass gets the first answer.</summary>
/// <remarks>The order rides beside the shipment so a carrier's log reads back to its order (ADR-129).</remarks>
public sealed record BookingRequest(ShipmentId ShipmentId, OrderId OrderId, DeliveryAddress Address)
{
    public string IdempotencyKey => $"book:{ShipmentId.Value}";
}
