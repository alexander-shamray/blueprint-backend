namespace Shipping.Domain.Shipments;

/// <summary>§5.2's typed identifier for the order a shipment answers for.</summary>
public readonly record struct OrderId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
