using System.Data;
using Common.Application;
using Dapper;

namespace Inventory.Application.Stock.GetAvailability;

/// <summary>§6.5's read side, Dapper over the write table, answering ADR-154's hop with a flag and no count.</summary>
public sealed class GetAvailabilityHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetAvailabilityQuery, IReadOnlyList<ProductAvailabilityDto>>
{
    // Available is already net of Reserved (StockItem.SetOnHand), so above zero is what can be bought.
    private const string Sql =
        """
        SELECT ProductId, InStock = CAST(CASE WHEN Available > 0 THEN 1 ELSE 0 END AS bit)
        FROM inventory.StockItems
        WHERE ProductId IN @ProductIds;
        """;

    public async Task<IReadOnlyList<ProductAvailabilityDto>> HandleAsync(
        GetAvailabilityQuery query,
        CancellationToken ct)
    {
        // No ids is a legal request, answered without a round trip; the ceiling is GetAvailabilityValidator's.
        if (query.ProductIds.Count == 0)
            return [];

        using IDbConnection connection = connections.Create();

        IEnumerable<ProductAvailabilityDto> rows = await connection.QueryAsync<ProductAvailabilityDto>(
            new CommandDefinition(Sql, new { ProductIds = query.ProductIds.Distinct() }, cancellationToken: ct));

        return [.. rows];
    }
}
