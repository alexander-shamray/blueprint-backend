using Common.Application;
using Common.Contracts.Ordering.V1;
using Payments.Application.Orders.EndErasedOrder;
using Payments.Application.Privacy;

namespace Payments.Application.Orders.RecordOrderPlaced;

/// <summary>§3.2's subscription: the payer arrives here, never on <c>AuthorisePayment</c> (ADR-028).</summary>
/// <remarks>
/// Dispatches, so the write runs in the transaction <see cref="IPaymentOrderStore"/> requires. Asked after that unit
/// commits, so an erasure at any moment finds the record or is seen (ADR-097).
/// </remarks>
public sealed class OrderPlacedHandler(IDispatcher dispatcher, IErasedSubjects erased)
    : IIntegrationEventHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced integrationEvent, CancellationToken ct)
    {
        IReadOnlyList<Guid> placedAfter = integrationEvent.ErasedUnder ?? [];

        await dispatcher.SendAsync(
            new RecordOrderPlacedCommand(
                integrationEvent.OrderId,
                integrationEvent.CustomerId,
                integrationEvent.TotalAmount,
                integrationEvent.Currency,
                integrationEvent.OccurredAt,
                placedAfter),
            ct);

        if (await erased.IsErasedAsync(
                integrationEvent.CustomerId, placedAfter, integrationEvent.OccurredAt, ct))
        {
            await dispatcher.SendAsync(
                new EndErasedOrderCommand(integrationEvent.OrderId, integrationEvent.CustomerId),
                ct);
        }
    }
}
