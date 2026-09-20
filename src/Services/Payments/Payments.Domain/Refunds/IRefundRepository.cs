using Payments.Domain.Orders;

namespace Payments.Domain.Refunds;

/// <summary>
/// The cancellation path's write side: one existence check and one staged add.
/// The check is what keeps a redelivery from voiding twice (spec, section 6).
/// </summary>
public interface IRefundRepository
{
    Task<bool> ExistsAsync(OrderId id, CancellationToken ct);

    void Add(Refund refund);
}
