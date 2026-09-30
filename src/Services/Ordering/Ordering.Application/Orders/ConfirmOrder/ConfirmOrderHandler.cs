using Common.Application;
using Common.Domain;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.ConfirmOrder;

/// <summary>The transition §9.6's saga asks for on payment; <see cref="Order.ConfirmPayment"/> owns it.</summary>
/// <remarks>No ownership check: no customer and no HTTP route, only <c>ordering-commands</c> (§9.4).</remarks>
public sealed class ConfirmOrderHandler(IOrderRepository orders, TimeProvider clock)
    : ICommandHandler<ConfirmOrderCommand, Result>
{
    public async Task<Result> HandleAsync(ConfirmOrderCommand command, CancellationToken ct)
    {
        Order? order = await orders.GetAsync(new OrderId(command.OrderId), ct);
        if (order is null)
            return Result.Failure(OrderErrors.NotFound);

        // Stock and payment arrive on two endpoints with no order between them, so this can be early: Unavailable
        // is retried by the endpoint's backoff, where a Rule error would ack a paid order unconfirmed (§9.8).
        if (order.Status is OrderStatus.AwaitingStock)
            return Result.Failure(OrderErrors.StockNotConfirmed);

        try
        {
            order.ConfirmPayment(command.Reference, clock.GetUtcNow());
        }
        catch (DomainException)
        {
            // Every other state refuses identically on every attempt: an answer, not a fault (§9.8).
            return Result.Failure(OrderErrors.NotAwaitingPayment);
        }

        // TransactionBehavior owns the commit and stages OrderConfirmedDomainEvent, hence a command (§6.3, §7.5).
        return Result.Success();
    }
}
