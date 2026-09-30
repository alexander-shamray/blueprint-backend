using Microsoft.EntityFrameworkCore;
using Payments.Domain.Orders;
using Payments.Domain.Refunds;

namespace Payments.Infrastructure.Persistence;

/// <summary>No lock of its own: the handler's lock on the order's record serialises this with authorisation.</summary>
internal sealed class RefundRepository(PaymentsDbContext db) : IRefundRepository
{
    public Task<bool> ExistsAsync(OrderId id, CancellationToken ct) => db.Refunds.AnyAsync(r => r.Id == id, ct);

    public void Add(Refund refund) => db.Add(refund);
}
