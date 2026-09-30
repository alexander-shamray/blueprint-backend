using Common.Application;
using Common.Contracts.Ordering.V1;

namespace Shipping.Application.Orders.RecordOrderCancelled;

/// <summary>Voids an unbooked shipment or records the request on a booked one; a worker tells the carrier.</summary>
public sealed class OrderCancelledHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderCancelled>
{
    public async Task HandleAsync(OrderCancelled integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(new VoidShipmentCommand(integrationEvent.OrderId), ct);
}
