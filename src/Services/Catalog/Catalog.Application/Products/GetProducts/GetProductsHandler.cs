using System.Data;
using Common.Application;
using Dapper;

namespace Catalog.Application.Products.GetProducts;

/// <summary>
/// §6.5's read side over the write tables, left-joining Inventory's projected level (§6.1, §3.2), by a keyset
/// seek whose <c>Id</c> tiebreaker keeps rows sharing a <c>PublishedAt</c> from straddling a page boundary.
/// </summary>
public sealed class GetProductsHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetProductsQuery, CursorPage<ProductSummaryDto>>
{
    /// <summary>The list's ceiling (§6.5): a larger <c>limit</c> is clamped to it, never refused.</summary>
    public const int MaxLimit = 100;

    private const string Sql =
        """
        SELECT TOP (@Take)
            ProductId         = p.Id,
            Name              = p.Name,
            ThumbnailUrl      = p.ThumbnailUrl,
            Amount            = p.PriceAmount,
            Currency          = p.PriceCurrency,
            PublishedAt       = p.PublishedAt,
            QuantityAvailable = s.QuantityAvailable
        FROM catalog.Products p
        LEFT JOIN catalog.StockLevels s ON s.ProductId = p.Id
        WHERE (@AfterPublishedAt IS NULL
            OR p.PublishedAt < @AfterPublishedAt
            OR (p.PublishedAt = @AfterPublishedAt AND p.Id < @AfterId))
        ORDER BY p.PublishedAt DESC, p.Id DESC;
        """;

    public async Task<CursorPage<ProductSummaryDto>> HandleAsync(GetProductsQuery query, CancellationToken ct)
    {
        int limit = Math.Clamp(query.Limit, 1, MaxLimit);
        (DateTimeOffset PublishedAt, Guid Id)? after = Cursor.Decode(query.Cursor);
        using IDbConnection connection = connections.Create();

        // One extra row says whether a next page exists, without a COUNT(*).
        List<ProductSummaryDto> rows = (await connection.QueryAsync<ProductSummaryDto>(
            new CommandDefinition(
                Sql,
                new
                {
                    Take = limit + 1,
                    AfterPublishedAt = after?.PublishedAt,
                    AfterId = after?.Id
                },
                cancellationToken: ct))).AsList();

        bool hasMore = rows.Count > limit;
        List<ProductSummaryDto> items = hasMore ? rows.GetRange(0, limit) : rows;
        string? next = hasMore && items.Count > 0
            ? Cursor.Encode(items[^1].PublishedAt, items[^1].ProductId)
            : null;

        return new CursorPage<ProductSummaryDto>(items, next);
    }
}
