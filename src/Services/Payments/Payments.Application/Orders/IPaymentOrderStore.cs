using Payments.Domain.Orders;

namespace Payments.Application.Orders;

/// <summary>Payments' record of the order (§3.2), in raw statements on the unit of work's transaction.</summary>
/// <remarks>A port, not <c>IUnitOfWork.ExecuteRawAsync</c>, since <see cref="LockAsync"/> returns a row.</remarks>
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

    /// <summary>Replaces the subject's payer id with the empty id on every record and returns how many (ADR-092).</summary>
    Task<int> AnonymiseCustomerAsync(Guid subjectId, CancellationToken ct);
}
