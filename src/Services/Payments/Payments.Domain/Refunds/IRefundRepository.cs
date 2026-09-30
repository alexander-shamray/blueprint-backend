using Payments.Domain.Orders;

namespace Payments.Domain.Refunds;

/// <summary>The cancellation path's write side; the existence check keeps a redelivery from voiding twice.</summary>
public interface IRefundRepository
{
    Task<bool> ExistsAsync(OrderId id, CancellationToken ct);

    void Add(Refund refund);
}
