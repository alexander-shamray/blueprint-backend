using Ordering.Domain.Common;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders;

/// <summary>The prices <c>PlaceOrderHandler</c> builds its lines from (§6.4).</summary>
/// <remarks>
/// A port over a local projection, never a gRPC call inside the write transaction (§6.3, ADR-002). A product never
/// priced in the asked-for currency has no row, and the order is refused (§6.6).
/// </remarks>
public interface IProductPriceReader
{
    Task<IReadOnlyDictionary<ProductId, Money>> GetAsync(
        IReadOnlyCollection<ProductId> productIds,
        string currency,
        CancellationToken ct);
}
