using Common.Application;

namespace Catalog.Application.Products.GetPrices;

/// <summary>The prices of a known set of products in one currency, for §9.7's hop.</summary>
/// <remarks>
/// Not paginated: the caller enumerates the ids, and §6.5 names this query as its exception. The list's ceiling
/// is <see cref="GetPricesValidator"/>'s.
/// </remarks>
public sealed record GetPricesQuery(IReadOnlyCollection<Guid> ProductIds, string Currency)
    : IQuery<IReadOnlyList<ProductPriceDto>>;
