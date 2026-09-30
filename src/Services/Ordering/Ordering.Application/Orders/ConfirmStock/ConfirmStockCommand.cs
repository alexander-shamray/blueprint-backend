using Common.Application;

namespace Ordering.Application.Orders.ConfirmStock;

/// <summary>Record that Inventory has held stock, from <c>StockReservedHandler</c>; not a contract (§3.2).</summary>
/// <remarks>Dispatched, so the work runs in §6.3's pipeline, where its domain event is staged (§7.5).</remarks>
public sealed record ConfirmStockCommand(Guid OrderId) : ICommand<Result>;
