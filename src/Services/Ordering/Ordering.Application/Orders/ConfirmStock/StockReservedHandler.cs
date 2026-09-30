using Common.Application;
using Common.Contracts.Inventory.V1;

namespace Ordering.Application.Orders.ConfirmStock;

/// <summary>Ordering's own reaction to <c>StockReserved</c>, recording on the order what the saga reads.</summary>
/// <remarks>Dispatches rather than mutating, for the reason <see cref="ConfirmStockCommand"/> gives (§9.6).</remarks>
public sealed class StockReservedHandler(IDispatcher dispatcher)
    : IIntegrationEventHandler<StockReserved>
{
    public async Task HandleAsync(StockReserved integrationEvent, CancellationToken ct)
    {
        // The Result is dropped, as CommandConsumer drops one (§9.8): every failure here is an answer, not a fault,
        // and LoggingBehavior records it (§13.3).
        await dispatcher.SendAsync(new ConfirmStockCommand(integrationEvent.OrderId), ct);
    }
}
