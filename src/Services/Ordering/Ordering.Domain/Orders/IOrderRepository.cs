namespace Ordering.Domain.Orders;

/// <summary>Collection-like access to the aggregate root (§5.6), implemented in Infrastructure.</summary>
/// <remarks>Only the members this service uses; no <c>Update</c>, and reads go through Dapper (§6.5).</remarks>
public interface IOrderRepository
{
    Task<Order?> GetAsync(OrderId id, CancellationToken ct);

    void Add(Order order);
}
