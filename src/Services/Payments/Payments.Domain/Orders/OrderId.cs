namespace Payments.Domain.Orders;

/// <summary>§5.2's typed identifier for the order a payment answers for.</summary>
public readonly record struct OrderId(Guid Value)
{
    public static OrderId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
