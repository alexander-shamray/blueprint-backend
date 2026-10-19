using Common.Application;
using Payments.Domain.Orders;

namespace Payments.Application.Orders.EndErasedOrder;

/// <summary>Anonymises the one order's payer, as the erasure would have had the order arrived before it.</summary>
/// <remarks>Scoped to the order, since the subject may have placed others after the erasure (ADR-099).</remarks>
public sealed class EndErasedOrderHandler(IPaymentOrderStore orders)
    : ICommandHandler<EndErasedOrderCommand, Result>
{
    public async Task<Result> HandleAsync(EndErasedOrderCommand command, CancellationToken ct)
    {
        await orders.AnonymiseOrderAsync(new OrderId(command.OrderId), command.CustomerId, ct);

        return Result.Success();
    }
}
