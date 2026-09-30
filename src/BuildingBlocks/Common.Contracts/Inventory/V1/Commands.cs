namespace Common.Contracts.Inventory.V1;

/// <summary>Sent by the saga to <c>inventory-commands</c> (§9.6).</summary>
public sealed record ReserveStock(Guid OrderId, IReadOnlyList<StockLine> Lines);

/// <summary>A postcondition, not an undo: it always publishes <c>StockReleased</c> (ADR-024).</summary>
public sealed record ReleaseStock(Guid OrderId);

/// <summary>No price, so Inventory's command never versions with Ordering's event (§9.6).</summary>
public sealed record StockLine(Guid ProductId, int Quantity);
