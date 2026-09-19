using Payments.Domain.Orders;

namespace Payments.Application.Orders;

/// <summary>
/// Payments' record of the order (§3.2): the payer, the total and the currency
/// from <c>OrderPlaced</c>, and the instant of <c>OrderCancelled</c>. Raw
/// statements on the unit of work's transaction, following
/// <c>IUnitOfWork.ExecuteRawAsync</c>'s rule for a table with no aggregate; a
/// port rather than that member because <see cref="LockAsync"/> returns the row
/// it locked.
/// </summary>
public interface IPaymentOrderStore
{
    Task RecordPlacedAsync(
        OrderId id,
        Guid customerId,
        decimal total,
        string currency,
        DateTimeOffset placedAt,
        CancellationToken ct);

    Task RecordCancelledAsync(OrderId id, DateTimeOffset cancelledAt, CancellationToken ct);

    Task<PaymentOrderRecord?> LockAsync(OrderId id, CancellationToken ct);
}
