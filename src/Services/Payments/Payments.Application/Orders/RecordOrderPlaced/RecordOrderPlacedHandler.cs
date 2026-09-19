using Common.Application;
using Payments.Domain.Orders;

namespace Payments.Application.Orders.RecordOrderPlaced;

public sealed class RecordOrderPlacedHandler(IPaymentOrderStore orders)
    : ICommandHandler<RecordOrderPlacedCommand, Result>
{
    public async Task<Result> HandleAsync(RecordOrderPlacedCommand command, CancellationToken ct)
    {
        await orders.RecordPlacedAsync(
            new OrderId(command.OrderId),
            command.CustomerId,
            command.TotalAmount,
            command.Currency,
            command.PlacedAt,
            ct);

        return Result.Success();
    }
}
