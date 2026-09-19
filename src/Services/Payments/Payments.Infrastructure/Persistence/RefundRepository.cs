using Microsoft.EntityFrameworkCore;
using Payments.Domain.Orders;
using Payments.Domain.Refunds;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// A plain read and a staged add: the handler has already stamped the order's
/// record, which is the lock that serialises this against the authorise path
/// (spec, section 6).
/// </summary>
internal sealed class RefundRepository(PaymentsDbContext db) : IRefundRepository
{
    public Task<bool> ExistsAsync(OrderId id, CancellationToken ct) => db.Refunds.AnyAsync(r => r.Id == id, ct);

    public void Add(Refund refund) => db.Add(refund);
}
