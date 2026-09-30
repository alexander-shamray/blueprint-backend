using Common.Application;
using Common.Domain;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.ConfirmStock;

/// <summary>
/// Moves the order from <c>AwaitingStock</c> to <c>AwaitingPayment</c> (§5.4).
/// </summary>
public sealed class ConfirmStockHandler(IOrderRepository orders, TimeProvider clock)
    : ICommandHandler<ConfirmStockCommand, Result>
{
    public async Task<Result> HandleAsync(ConfirmStockCommand command, CancellationToken ct)
    {
        Order? order = await orders.GetAsync(new OrderId(command.OrderId), ct);
        if (order is null)
            return Result.Failure(OrderErrors.NotFound);

        try
        {
            order.ConfirmStock(clock.GetUtcNow());
        }
        catch (DomainException)
        {
            // Not Unavailable: nothing earlier can be in flight, so the order has moved on (usually Cancelled).
            return Result.Failure(OrderErrors.NotAwaitingStock);
        }

        return Result.Success();
    }
}
