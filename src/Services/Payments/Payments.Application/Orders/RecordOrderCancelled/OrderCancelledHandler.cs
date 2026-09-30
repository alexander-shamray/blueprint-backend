using Common.Application;
using Common.Contracts.Ordering.V1;

namespace Payments.Application.Orders.RecordOrderCancelled;

/// <summary>Records the cancellation, which voids an authorisation (§9.6) and refuses a later one (ADR-049).</summary>
public sealed class OrderCancelledHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderCancelled>
{
    public async Task HandleAsync(OrderCancelled integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordOrderCancelledCommand(integrationEvent.OrderId, integrationEvent.OccurredAt),
            ct);
}
