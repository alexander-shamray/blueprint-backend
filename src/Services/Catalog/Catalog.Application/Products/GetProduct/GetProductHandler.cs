using System.Data;
using Catalog.Application.Products.GetProducts;
using Common.Application;
using Dapper;

namespace Catalog.Application.Products.GetProduct;

/// <summary>
/// §6.5's read side over the write tables: the listing's select, narrowed to one id, so a withdrawn product is the
/// same 404 as an unknown one (ADR-074).
/// </summary>
/// <remarks>
/// Through <see cref="IProductReadCache"/> (§8.2), unlike the listing: this row's invalidators are the price and
/// withdrawal projections and <c>StockLevelProjection</c> (§8.4, E.3.3).
/// </remarks>
public sealed class GetProductHandler(IDbConnectionFactory connections, IProductReadCache cache)
    : IQueryHandler<GetProductQuery, Result<ProductSummaryDto>>
{
    private const string Sql =
        """
        SELECT
            ProductId         = p.Id,
            Name              = p.Name,
            ThumbnailUrl      = p.ThumbnailUrl,
            Amount            = p.PriceAmount,
            Currency          = p.PriceCurrency,
            PublishedAt       = p.PublishedAt,
            QuantityAvailable = s.QuantityAvailable
        FROM catalog.Products p
        LEFT JOIN catalog.StockLevels s ON s.ProductId = p.Id
        WHERE p.Id = @ProductId
          AND p.WithdrawnAt IS NULL;
        """;

    public async Task<Result<ProductSummaryDto>> HandleAsync(GetProductQuery query, CancellationToken ct)
    {
        ProductSummaryDto? product = await cache.GetOrLoadAsync(
            query.ProductId,
            async token =>
            {
                using IDbConnection connection = connections.Create();

                return await connection.QuerySingleOrDefaultAsync<ProductSummaryDto>(
                    new CommandDefinition(Sql, new { query.ProductId }, cancellationToken: token));
            },
            ct);

        return product is null
            ? Result.Failure<ProductSummaryDto>(ProductErrors.NotFound)
            : Result.Success(product);
    }
}
