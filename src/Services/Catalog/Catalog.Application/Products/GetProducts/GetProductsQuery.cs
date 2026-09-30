using Common.Application;

namespace Catalog.Application.Products.GetProducts;

/// <summary>The catalogue listing, newest first, cursor-paginated as §6.5 and ADR-016 require.</summary>
public sealed record GetProductsQuery(string? Cursor, int Limit)
    : IQuery<CursorPage<ProductSummaryDto>>;
