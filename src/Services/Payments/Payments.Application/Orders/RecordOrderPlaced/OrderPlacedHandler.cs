using Common.Application;
using Common.Contracts.Ordering.V1;

namespace Payments.Application.Orders.RecordOrderPlaced;

/// <summary>
/// §3.2's subscription: the payer is bound from a real principal at Ordering's
/// endpoint (§11.4) and reaches Payments here, never on <c>AuthorisePayment</c>
/// (ADR-028).
/// </summary>
/// <remarks>
/// It dispatches rather than writing, so the write runs inside the command
/// pipeline's transaction, which is where the store refuses to run without.
/// </remarks>
public sealed class OrderPlacedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordOrderPlacedCommand(
                integrationEvent.OrderId,
                integrationEvent.CustomerId,
                integrationEvent.TotalAmount,
                integrationEvent.Currency,
                integrationEvent.OccurredAt),
            ct);
}
