using System.Data;
using Catalog.Application.Products.GetProducts;
using Common.Application;
using Dapper;

namespace Catalog.Application.Products.GetProduct;

/// <summary>§6.5's read side over the write tables: the listing's select, narrowed to one id.</summary>
/// <remarks>Uncached, as the listing is; §8.2 says why a cached copy would outlive its stock level.</remarks>
public sealed class GetProductHandler(IDbConnectionFactory connections)
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
        WHERE p.Id = @ProductId;
        """;

    public async Task<Result<ProductSummaryDto>> HandleAsync(GetProductQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        ProductSummaryDto? product = await connection.QuerySingleOrDefaultAsync<ProductSummaryDto>(
            new CommandDefinition(Sql, new { query.ProductId }, cancellationToken: ct));

        return product is null
            ? Result.Failure<ProductSummaryDto>(ProductErrors.NotFound)
            : Result.Success(product);
    }
}
