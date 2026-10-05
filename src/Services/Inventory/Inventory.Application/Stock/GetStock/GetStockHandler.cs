using System.Data;
using Common.Application;
using Dapper;

namespace Inventory.Application.Stock.GetStock;

/// <summary>§6.5's read side: Dapper over the write table.</summary>
public sealed class GetStockHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetStockQuery, Result<StockDto>>
{
    private const string Sql =
        """
        SELECT ProductId, Available, Reserved, UpdatedAt
        FROM inventory.StockItems
        WHERE ProductId = @ProductId;
        """;

    public async Task<Result<StockDto>> HandleAsync(GetStockQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        StockDto? stock = await connection.QuerySingleOrDefaultAsync<StockDto>(
            new CommandDefinition(Sql, new { query.ProductId }, cancellationToken: ct));

        return stock is null ? Result.Failure<StockDto>(StockErrors.NotFound) : Result.Success(stock);
    }
}
