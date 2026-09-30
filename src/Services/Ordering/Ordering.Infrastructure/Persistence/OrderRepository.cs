using Microsoft.EntityFrameworkCore;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>§5.6's implementation half, matching the port; reads are Dapper's (§6.5).</summary>
internal sealed class OrderRepository(OrderingDbContext db) : IOrderRepository
{
    /// <summary>With its lines, since every transition reads <c>Total</c> (§5.6).</summary>
    public Task<Order?> GetAsync(OrderId id, CancellationToken ct) =>
        db.Orders
            .Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == id, ct);

    public void Add(Order order) => db.Add(order);
}
