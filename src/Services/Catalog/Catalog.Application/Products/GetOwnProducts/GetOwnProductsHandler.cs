using System.Data;
using Catalog.Application.Products.GetProducts;
using Common.Application;
using Dapper;

namespace Catalog.Application.Products.GetOwnProducts;

/// <summary>
/// §6.5's read side over the write tables, narrowed to the caller's own rows by the seller index, withdrawn ones
/// included, with the listing's newest-first keyset seek (ADR-074).
/// </summary>
public sealed class GetOwnProductsHandler(IDbConnectionFactory connections, ICurrentUser currentUser)
    : IQueryHandler<GetOwnProductsQuery, CursorPage<OwnProductDto>>
{
    private const string Sql =
        """
        SELECT TOP (@Take)
            ProductId         = p.Id,
            Name              = p.Name,
            ThumbnailUrl      = p.ThumbnailUrl,
            Amount            = p.PriceAmount,
            Currency          = p.PriceCurrency,
            PublishedAt       = p.PublishedAt,
            WithdrawnAt       = p.WithdrawnAt,
            QuantityAvailable = s.QuantityAvailable
        FROM catalog.Products p
        LEFT JOIN catalog.StockLevels s ON s.ProductId = p.Id
        WHERE p.SellerId = @SellerId
          AND (@AfterPublishedAt IS NULL
            OR p.PublishedAt < @AfterPublishedAt
            OR (p.PublishedAt = @AfterPublishedAt AND p.Id < @AfterId))
        ORDER BY p.PublishedAt DESC, p.Id DESC;
        """;

    public async Task<CursorPage<OwnProductDto>> HandleAsync(GetOwnProductsQuery query, CancellationToken ct)
    {
        int limit = Math.Clamp(query.Limit, 1, GetProductsHandler.MaxLimit);

        // Any cursor this read did not mint, the listing's included, is unreadable here and so the first page.
        (DateTimeOffset SortKey, Guid Id)? after = Cursor.Decode(query.Cursor);
        using IDbConnection connection = connections.Create();

        // The endpoint's policy guarantees a subject, so Id throws only on a dispatch that bypassed it (§11.4).
        List<OwnProductDto> rows = (await connection.QueryAsync<OwnProductDto>(
            new CommandDefinition(
                Sql,
                new
                {
                    Take = limit + 1,
                    SellerId = currentUser.Id,
                    AfterPublishedAt = after?.SortKey,
                    AfterId = after?.Id
                },
                cancellationToken: ct))).AsList();

        // One extra row says whether a next page exists, without a COUNT(*).
        bool hasMore = rows.Count > limit;
        List<OwnProductDto> items = hasMore ? rows.GetRange(0, limit) : rows;
        string? next = hasMore && items.Count > 0 ? Cursor.Encode(items[^1].PublishedAt, items[^1].ProductId) : null;

        return new CursorPage<OwnProductDto>(items, next);
    }
}
