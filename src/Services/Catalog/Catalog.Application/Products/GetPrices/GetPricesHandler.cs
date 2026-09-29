using System.Data;
using Common.Application;
using Dapper;

namespace Catalog.Application.Products.GetPrices;

/// <summary>
/// §6.5's read side again — Dapper over the write tables, Catalog being level 1
/// — answering §9.7's pricing hop.
/// </summary>
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
        // Asking for no prices is a legal thing for a caller to do, and
        // answering it here saves a round trip that can only return nothing —
        // the same guard, for the same reason, as Ordering's
        // ProjectedPriceReader. NOT because Dapper would refuse the query:
        // measured against the pinned 2.1.66, an empty expansion becomes
        // IN (SELECT @Ids WHERE 1 = 0), which is valid and returns no rows.
        // The ceiling at the other end is GetPricesValidator's, because it is
        // a malformed request rather than an empty answer.
        if (query.ProductIds.Count == 0)
            return [];

        using IDbConnection connection = connections.Create();

        // Distinct, because the id list is the caller's and a repeated id would
        // otherwise cost a duplicate row the BFF would have to fold. The set is
        // the semantics the reply already has — one price per product.
        //
        // Upper-cased for the same reason Money.Of does it on the way in: the
        // column only ever holds the canonical form, so comparing the caller's
        // string as it arrived makes a valid request depend on the server's
        // collation. Under the default case-insensitive one "gbp" matches and
        // nothing is wrong; under a case-sensitive collation the same request
        // returns "product absent", which is the answer this query gives for a
        // product that does not exist. Normalising here is what keeps the two
        // ends agreeing rather than the database settings.
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
