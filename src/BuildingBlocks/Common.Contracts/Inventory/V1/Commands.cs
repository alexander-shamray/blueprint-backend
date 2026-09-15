namespace Common.Contracts.Inventory.V1;

/// <summary>
/// Hold stock for an order (§3.2's Accepts column), sent by the saga to
/// <c>inventory-commands</c> (§9.6).
/// </summary>
public sealed record ReserveStock(Guid OrderId, IReadOnlyList<StockLine> Lines);

/// <summary>
/// Establish that no stock is held for this order (§3.2), sent by the saga to
/// <c>inventory-commands</c> from every compensating transition (§9.6).
/// </summary>
/// <remarks>
/// A postcondition rather than an undo (ADR-024): the saga sends it on a
/// cancellation too, possibly before <see cref="ReserveStock"/> arrives. It
/// always publishes <c>StockReleased</c>, and a release for an unseen order is
/// remembered, so a later reserve is refused with <c>StockReleased</c>.
/// </remarks>
public sealed record ReleaseStock(Guid OrderId);

/// <summary>
/// A line as <see cref="ReserveStock"/> carries it.
/// </summary>
/// <remarks>
/// Not <c>PlacedLine</c>: reserving stock needs no price, and Inventory's
/// command must not have to change because Ordering versioned an event (§9.6).
/// The saga maps one to the other rather than forwarding.
/// </remarks>
public sealed record StockLine(Guid ProductId, int Quantity);
