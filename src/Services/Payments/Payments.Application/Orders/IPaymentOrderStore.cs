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
        IReadOnlyList<Guid> erasedUnder,
        CancellationToken ct);

    Task RecordCancelledAsync(OrderId id, DateTimeOffset cancelledAt, CancellationToken ct);

    Task<PaymentOrderRecord?> LockAsync(OrderId id, CancellationToken ct);

    /// <summary>
    /// Replaces the subject's payer id with the empty id on every record not placed after the request, and returns
    /// how many (ADR-092, ADR-099).
    /// </summary>
    Task<int> AnonymiseCustomerAsync(Guid subjectId, Guid requestId, CancellationToken ct);

    /// <summary>Replaces one record's payer id with the empty id while it is still the customer's (ADR-097).</summary>
    Task<int> AnonymiseOrderAsync(OrderId id, Guid customerId, CancellationToken ct);
}
