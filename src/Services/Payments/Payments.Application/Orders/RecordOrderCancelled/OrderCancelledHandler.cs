using Common.Application;
using Common.Contracts.Ordering.V1;

namespace Payments.Application.Orders.RecordOrderCancelled;

/// <summary>
/// Records the cancellation on Payments' record of the order, creating the
/// record when <c>OrderPlaced</c> has not arrived (§9.4 orders nothing), and
/// voids an authorisation taken for it (§9.6). The stamp is what refuses a
/// later <c>AuthorisePayment</c> (ADR-049).
/// </summary>
public sealed class OrderCancelledHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderCancelled>
{
    public async Task HandleAsync(OrderCancelled integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordOrderCancelledCommand(integrationEvent.OrderId, integrationEvent.OccurredAt),
            ct);
}
