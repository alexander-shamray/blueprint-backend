using Common.Application;
using Common.Domain;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.MarkOrderShipped;

/// <summary>The transition §9.6's saga asks for on <c>ShipmentDispatched</c>.</summary>
public sealed class MarkOrderShippedHandler(IOrderRepository orders, TimeProvider clock)
    : ICommandHandler<MarkOrderShippedCommand, Result>
{
    public async Task<Result> HandleAsync(MarkOrderShippedCommand command, CancellationToken ct)
    {
        Order? order = await orders.GetAsync(new OrderId(command.OrderId), ct);
        if (order is null)
            return Result.Failure(OrderErrors.NotFound);

        // ConfirmOrderHandler's split one state later: the confirmation is in flight, and §9.8's backoff waits.
        if (order.Status is OrderStatus.AwaitingStock or OrderStatus.AwaitingPayment)
            return Result.Failure(OrderErrors.NotConfirmed);

        try
        {
            order.MarkShipped(command.Tracking, clock.GetUtcNow());
        }
        catch (DomainException)
        {
            // Cancelled, Shipped or Delivered: no retry changes any of them.
            return Result.Failure(OrderErrors.NotShippable);
        }

        return Result.Success();
    }
}
