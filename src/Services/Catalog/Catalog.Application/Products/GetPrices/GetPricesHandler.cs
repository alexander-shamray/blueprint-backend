using System.Data;
using Common.Application;
using Dapper;

namespace Catalog.Application.Products.GetPrices;

/// <summary>§6.5's read side, Dapper over the write tables at level 1, answering §9.7's pricing hop.</summary>
public sealed class GetPricesHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetPricesQuery, IReadOnlyList<ProductPriceDto>>
{
    private const string Sql =
        """
        SELECT
            ProductId = p.Id,
            Name      = p.Name,
            Amount    = p.PriceAmount,
            Currency  = p.PriceCurrency
        FROM catalog.Products p
        WHERE p.Id IN @ProductIds
            AND p.PriceCurrency = @Currency;
        """;

    public async Task<IReadOnlyList<ProductPriceDto>> HandleAsync(GetPricesQuery query, CancellationToken ct)
    {
        // No ids is a legal request, answered without a round trip; the ceiling is GetPricesValidator's.
        if (query.ProductIds.Count == 0)
            return [];

        using IDbConnection connection = connections.Create();

        // Distinct, since the reply is one price per product. Upper-cased, as Money.Of stores it, so a valid
        // request does not depend on the server's collation.
        IEnumerable<ProductPriceDto> rows = await connection.QueryAsync<ProductPriceDto>(
            new CommandDefinition(
                Sql,
                new
                {
                    ProductIds = query.ProductIds.Distinct(),
                    Currency = query.Currency.ToUpperInvariant()
                },
                cancellationToken: ct));

        return [.. rows];
    }
}
