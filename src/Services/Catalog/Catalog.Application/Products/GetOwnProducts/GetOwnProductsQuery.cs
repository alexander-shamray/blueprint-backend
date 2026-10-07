using Common.Application;

namespace Catalog.Application.Products.GetOwnProducts;

/// <summary>
/// The caller's own products, newest first and cursor-paginated as §6.5 requires; the seller is the principal,
/// never a parameter (§11.4, ADR-074).
/// </summary>
public sealed record GetOwnProductsQuery(string? Cursor, int Limit) : IQuery<CursorPage<OwnProductDto>>;
