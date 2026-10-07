using Common.Application;

namespace Catalog.Application.Products.GetProducts;

/// <summary>
/// The catalogue listing, cursor-paginated as §6.5 and ADR-016 require, narrowed by <c>q</c> and ordered by
/// <c>sort</c> as ADR-073 decides.
/// </summary>
public sealed record GetProductsQuery(string? Cursor, int Limit, string? Q = null, string? Sort = null)
    : IQuery<CursorPage<ProductSummaryDto>>
{
    /// <summary>The text searched for, trimmed, and null where a blank <c>q</c> asks for no search.</summary>
    public string? Search => string.IsNullOrWhiteSpace(Q) ? null : Q.Trim();

    /// <summary>The ordering asked for (<see cref="ProductSort.Resolve"/>).</summary>
    public string Ordering => ProductSort.Resolve(Sort);
}
