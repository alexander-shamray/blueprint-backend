namespace Ordering.Domain.Orders;

/// <summary>§5.4's state machine as a closed set; only <see cref="Order"/> assigns one, by a named method.</summary>
/// <remarks>
/// Persisted by name (§7.2). <c>Delivered</c> has no transition; it exists for <see cref="Order.Cancel"/>'s guard,
/// which §5.4 writes over both terminal states.
/// </remarks>
public enum OrderStatus
{
    Draft,
    AwaitingStock,
    AwaitingPayment,
    Confirmed,
    Shipped,
    Delivered,
    Cancelled
}
