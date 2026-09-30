namespace Ordering.Domain.Orders;

/// <summary>A line's own identity, so a line can never be compared equal to its order (§5.5).</summary>
public readonly record struct OrderLineId(Guid Value)
{
    public static OrderLineId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
