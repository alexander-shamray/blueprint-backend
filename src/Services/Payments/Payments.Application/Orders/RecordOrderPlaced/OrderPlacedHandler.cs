using Common.Application;
using Common.Contracts.Ordering.V1;

namespace Payments.Application.Orders.RecordOrderPlaced;

/// <summary>§3.2's subscription: the payer arrives here, never on <c>AuthorisePayment</c> (ADR-028).</summary>
/// <remarks>Dispatches, so the write runs in the transaction <see cref="IPaymentOrderStore"/> requires.</remarks>
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
