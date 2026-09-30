using System.Data;
using Common.Application;
using Dapper;
using Ordering.Application.Orders;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>§6.4's price reader, over a local projection, so Catalog being down stops no order (ADR-002).</summary>
/// <remarks>A product with no row in the asked-for currency is refused as unavailable, which §6.6 names.</remarks>
internal sealed class ProjectedPriceReader(IDbConnectionFactory connections) : IProductPriceReader
{
    private const string Sql =
        """
        SELECT ProductId, Amount, Currency
        FROM ordering.ProductPrices
        WHERE ProductId IN @ProductIds
            AND Currency = @Currency
            AND IsAvailable = 1;
        """;

    public async Task<IReadOnlyDictionary<ProductId, Money>> GetAsync(
        IReadOnlyCollection<ProductId> productIds,
        string currency,
        CancellationToken ct)
    {
        // An optimisation, not a repair: Dapper expands an empty list to a valid query returning nothing.
        if (productIds.Count == 0)
            return new Dictionary<ProductId, Money>();

        using IDbConnection connection = connections.Create();

        // Upper-cased, as the projection writes it, so a lower-case request cannot depend on collation (§6.4).
        IEnumerable<PriceRow> rows = await connection.QueryAsync<PriceRow>(
            new CommandDefinition(
                Sql,
                new
                {
                    ProductIds = productIds.Select(p => p.Value),
                    Currency = currency.ToUpperInvariant()
                },
                cancellationToken: ct));

        return rows.ToDictionary(r => new ProductId(r.ProductId), r => Money.Of(r.Amount, r.Currency));
    }

    /// <summary>Dapper's target; <c>Money.Of</c> is where the row becomes a value object, or throws.</summary>
    private sealed record PriceRow(Guid ProductId, decimal Amount, string Currency);
}
