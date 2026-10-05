using Catalog.Application.Products.GetProducts;
using Common.Application;

namespace Catalog.Application.Products.GetProduct;

/// <summary>One product by id, as the listing's row, for a product route's deep link (§6.5).</summary>
public sealed record GetProductQuery(Guid ProductId) : IQuery<Result<ProductSummaryDto>>;
