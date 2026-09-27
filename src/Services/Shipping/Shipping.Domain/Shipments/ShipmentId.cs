namespace Shipping.Domain.Shipments;

/// <summary>§5.2's typed identifier for a shipment.</summary>
public readonly record struct ShipmentId(Guid Value)
{
    public static ShipmentId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
