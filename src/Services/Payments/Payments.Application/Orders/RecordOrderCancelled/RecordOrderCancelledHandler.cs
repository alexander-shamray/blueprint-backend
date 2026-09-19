using Common.Application;
using Payments.Domain.Orders;

namespace Payments.Application.Orders.RecordOrderCancelled;

public sealed class RecordOrderCancelledHandler(IPaymentOrderStore orders)
    : ICommandHandler<RecordOrderCancelledCommand, Result>
{
    public async Task<Result> HandleAsync(RecordOrderCancelledCommand command, CancellationToken ct)
    {
        await orders.RecordCancelledAsync(new OrderId(command.OrderId), command.CancelledAt, ct);

        return Result.Success();
    }
}
