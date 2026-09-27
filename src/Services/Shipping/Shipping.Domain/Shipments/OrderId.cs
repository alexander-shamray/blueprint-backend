namespace Shipping.Domain.Shipments;

/// <summary>
/// §5.2's typed identifier for the order a shipment answers for. Shipping's
/// own type rather than Ordering's: an identifier crossing a context boundary
/// arrives as a primitive (§9.1) and is given this service's meaning here.
/// </summary>
public readonly record struct OrderId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
