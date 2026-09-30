namespace Ordering.Domain.Orders;

/// <summary>The order's owner, the one thing §11.4's ownership check compares, referenced by id (§5.4).</summary>
/// <remarks>No <c>New()</c>: Ordering never mints a customer, and a factory would invent a subject (§11.4).</remarks>
public readonly record struct CustomerId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
