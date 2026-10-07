using System.Data;
using Common.Application;
using Dapper;

namespace Catalog.Application.Products.GetProducts;

/// <summary>
/// §6.5's read side over the write tables, left-joining Inventory's projected level (§6.1, §3.2), by a keyset
/// seek per ordering whose <c>Id</c> tiebreaker keeps rows sharing a sort key from straddling a page boundary,
/// narrowed by ADR-073's search, and with no withdrawn product in it (ADR-074).
/// </summary>
public sealed class GetProductsHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetProductsQuery, CursorPage<ProductSummaryDto>>
{
    /// <summary>The list's ceiling (§6.5): a larger <c>limit</c> is clamped to it, never refused.</summary>
    public const int MaxLimit = 100;

    private const string Columns =
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
        WHERE p.WithdrawnAt IS NULL
          AND (@Pattern IS NULL OR p.Name LIKE @Pattern ESCAPE '\')
        """;

    private const string NewestSql =
        Columns +
        """

          AND (@AfterPublishedAt IS NULL
            OR p.PublishedAt < @AfterPublishedAt
            OR (p.PublishedAt = @AfterPublishedAt AND p.Id < @AfterId))
        ORDER BY p.PublishedAt DESC, p.Id DESC;
        """;

    // The comparison and the ORDER BY both run in the column's collation, so the seek agrees with the order.
    private const string NameSql =
        Columns +
        """

          AND (@AfterName IS NULL
            OR p.Name > @AfterName
            OR (p.Name = @AfterName AND p.Id > @AfterId))
        ORDER BY p.Name ASC, p.Id ASC;
        """;

    public async Task<CursorPage<ProductSummaryDto>> HandleAsync(GetProductsQuery query, CancellationToken ct)
    {
        int limit = Math.Clamp(query.Limit, 1, MaxLimit);
        string sort = query.Ordering;
        string? search = query.Search;

        // GetProductsValidator has refused a readable cursor minted under another query, so one read here is ours.
        ProductCursor? after = ProductCursor.Decode(query.Cursor);
        using IDbConnection connection = connections.Create();

        // One extra row says whether a next page exists, without a COUNT(*).
        List<ProductSummaryDto> rows = (await connection.QueryAsync<ProductSummaryDto>(
            new CommandDefinition(
                sort == ProductSort.Name ? NameSql : NewestSql,
                new
                {
                    Take = limit + 1,
                    Pattern = Pattern(search),
                    AfterPublishedAt = after?.PublishedAt,
                    AfterName = after?.Name,
                    AfterId = after?.Id
                },
                cancellationToken: ct))).AsList();

        bool hasMore = rows.Count > limit;
        List<ProductSummaryDto> items = hasMore ? rows.GetRange(0, limit) : rows;
        string? next = hasMore && items.Count > 0 ? Next(sort, search, items[^1]).Encode() : null;

        return new CursorPage<ProductSummaryDto>(items, next);
    }

    private static ProductCursor Next(string sort, string? search, ProductSummaryDto last) =>
        sort == ProductSort.Name
            ? ProductCursor.ForName(search, last.Name, last.ProductId)
            : ProductCursor.ForNewest(search, last.PublishedAt, last.ProductId);

    // A contains-match on text the caller typed, never a pattern the caller wrote: each LIKE metacharacter is
    // escaped, the escape character first so the ones added after it are not escaped twice.
    private static string? Pattern(string? search) =>
        search is null
            ? null
            : "%" + search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[") + "%";
}
