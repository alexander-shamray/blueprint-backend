using Common.Application;
using Common.Contracts.Ordering.V1;

namespace Shipping.Application.Orders.RecordOrderConfirmed;

/// <summary>§3.2's subscription, dispatching so the write runs in the pipeline's transaction (§6.3).</summary>
/// <remarks><c>OrderConfirmed</c> carries no address (ADR-035); the worker asks Ordering for one (ADR-052).</remarks>
public sealed class OrderConfirmedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderConfirmed>
{
    public async Task HandleAsync(OrderConfirmed integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(new CreateShipmentCommand(integrationEvent.OrderId), ct);
}
