using Common.Application;
using Common.Contracts.Ordering.V1;

namespace Shipping.Application.Orders.RecordOrderConfirmed;

/// <summary>
/// §3.2's first subscription. It dispatches rather than writing, so the write
/// runs inside the command pipeline's transaction (§6.3), and it reads no
/// address: <c>OrderConfirmed</c> carries none (ADR-035), and the worker asks
/// Ordering for one (ADR-052).
/// </summary>
public sealed class OrderConfirmedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderConfirmed>
{
    public async Task HandleAsync(OrderConfirmed integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(new CreateShipmentCommand(integrationEvent.OrderId), ct);
}
