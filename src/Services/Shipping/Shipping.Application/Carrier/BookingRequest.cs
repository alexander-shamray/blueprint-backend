using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>One shipment offered to the carrier, keyed so a repeated pass gets the first answer.</summary>
public sealed record BookingRequest(ShipmentId ShipmentId, DeliveryAddress Address)
{
    public string IdempotencyKey => $"book:{ShipmentId.Value}";
}
