using Common.Application;
using Common.Contracts.Ordering.V1;
using Inventory.Application.Reservations.ReleaseStock;

namespace Inventory.Application.Reservations.Integration;

/// <summary>§3.2's derivation, ADR-029's decision: a cancellation releases directly.</summary>
public sealed class OrderCancelledHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderCancelled>
{
    public async Task HandleAsync(OrderCancelled integrationEvent, CancellationToken ct)
    {
        await dispatcher.SendAsync(new ReleaseStockCommand(integrationEvent.OrderId, CommandOrigin.System), ct);
    }
}
