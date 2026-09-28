using Common.Application;
using Common.Contracts.Ordering.V1;

namespace Shipping.Application.Orders.RecordOrderCancelled;

/// <summary>
/// Voids a shipment that has not been booked and records the request against
/// one that has (spec, sections 5 and 6); the carrier is told by a worker and
/// never from here (spec, section 4).
/// </summary>
public sealed class OrderCancelledHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderCancelled>
{
    public async Task HandleAsync(OrderCancelled integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(new VoidShipmentCommand(integrationEvent.OrderId), ct);
}
