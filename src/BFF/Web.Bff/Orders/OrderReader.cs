using System.Data;
using System.Text.Json;
using Common.Application;
using Dapper;

namespace Web.Bff.Orders;

/// <summary>E.6.10's two reads over ADR-051's projection, bound to a customer the caller never names.</summary>
public sealed class OrderReader(IDbConnectionFactory connections)
{
    /// <summary>The columns both reads select, so the two routes cannot read different facts about one row.</summary>
    private const string Columns =
        """
        o.OrderId, o.Currency, o.TotalAmount, o.PlacedAt, o.ConfirmedAt, o.DispatchedAt, o.DeliveredAt,
        o.CancelledAt, o.CancelOutcome, o.AuthorisedAt, o.AuthorisedAmount, o.RefundedAt, o.RefundedAmount,
        o.PaymentCurrency, o.TrackingNumber, o.FirstSeenAt, o.AsOf
        """;

    /// <summary>What both pages select, narrowed to the buyer's rows of <c>IX_Orders_Owned</c>.</summary>
    private const string Owned =
        $"""
        SELECT TOP (@Take) {Columns}
        FROM bff.Orders o
        -- IS NOT NULL repeats the index's filter, so the seek matches it whatever the parameter.
        WHERE o.CustomerId = @CustomerId
            AND o.CustomerId IS NOT NULL
        """;

    /// <summary>The index's own order, so the walk needs no sort; the id breaks a tie across a page.</summary>
    private const string Newest =
        """

        ORDER BY o.FirstSeenAt DESC, o.OrderId DESC;
        """;

    // A cursor's page has a statement of its own, so the seek reaches the cursor rather than the buyer's newest row;
    // an "@After IS NULL OR" predicate cannot seek, as ADR-179 found for Catalog's.
    internal const string FirstPageSql = Owned + Newest;

    internal const string AfterPageSql =
        Owned +
        """

            AND (o.FirstSeenAt < @AfterFirstSeenAt
                OR (o.FirstSeenAt = @AfterFirstSeenAt AND o.OrderId < @AfterId))
        """ +
        Newest;

    /// <summary>Every line and name on the page in one statement, the ids as one JSON parameter (§6.6).</summary>
    private const string PageLinesSql =
        """
        SELECT l.OrderId, l.LineNumber, l.ProductId, l.Quantity, l.UnitPrice, ProductName = p.Name
        FROM bff.OrderLines l
        INNER JOIN OPENJSON(@OrderIds) j
            ON l.OrderId = CAST(j.value AS uniqueidentifier)
        LEFT JOIN bff.Products p
            ON p.ProductId = l.ProductId;
        """;

    /// <summary>The row and its lines, each filtered on id and customer, so another's order reads as none.</summary>
    private const string DetailSql =
        $"""
        SELECT {Columns}
        FROM bff.Orders o
        WHERE o.OrderId = @OrderId
            AND o.CustomerId = @CustomerId;

        SELECT l.OrderId, l.LineNumber, l.ProductId, l.Quantity, l.UnitPrice, ProductName = p.Name
        FROM bff.OrderLines l
        INNER JOIN bff.Orders o
            ON o.OrderId = l.OrderId
            AND o.CustomerId = @CustomerId
        LEFT JOIN bff.Products p
            ON p.ProductId = l.ProductId
        WHERE l.OrderId = @OrderId;
        """;

    /// <summary>What a stream asks each interval (ADR-108): one key read, and the detail only when this
    /// moved.</summary>
    private const string AsOfSql =
        """
        SELECT o.AsOf
        FROM bff.Orders o
        WHERE o.OrderId = @OrderId
            AND o.CustomerId = @CustomerId;
        """;

    public async Task<CursorPage<OrderSummary>> ListAsync(
        Guid customerId,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        int take = OrderPage.Clamp(limit);
        (DateTimeOffset SortKey, Guid Id)? after = Cursor.Decode(cursor);

        using IDbConnection connection = connections.Create();

        // One extra row says whether a next page exists, without a COUNT(*).
        List<OrderReadRow> rows = (await connection.QueryAsync<OrderReadRow>(
            new CommandDefinition(
                after is null ? FirstPageSql : AfterPageSql,
                new
                {
                    CustomerId = customerId,
                    Take = take + 1,
                    AfterFirstSeenAt = after?.SortKey,
                    AfterId = after?.Id
                },
                cancellationToken: ct))).AsList();

        bool hasMore = rows.Count > take;
        List<OrderReadRow> page = hasMore ? rows.GetRange(0, take) : rows;

        ILookup<Guid, OrderLineReadRow> lines = await PageLinesAsync(connection, page, ct);

        OrderSummary[] items = [.. page.Select(row => OrderView.Summary(row, [.. lines[row.OrderId]]))];

        string? next = hasMore ? Cursor.Encode(page[^1].FirstSeenAt, page[^1].OrderId) : null;

        return new CursorPage<OrderSummary>(items, next);
    }

    public async Task<Result<OrderDetail>> FindAsync(Guid customerId, Guid orderId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        using SqlMapper.GridReader grid = await connection.QueryMultipleAsync(
            new CommandDefinition(
                DetailSql,
                new { OrderId = orderId, CustomerId = customerId },
                cancellationToken: ct));

        OrderReadRow? row = await grid.ReadSingleOrDefaultAsync<OrderReadRow>();
        List<OrderLineReadRow> lines = (await grid.ReadAsync<OrderLineReadRow>()).AsList();

        return row is null
            ? Result.Failure<OrderDetail>(OrderReadErrors.NotFound)
            : Result.Success(OrderView.Detail(row, lines));
    }

    /// <summary>The row's <c>AsOf</c> alone, filtered on id and customer; null for none, as <see cref="FindAsync"/>
    /// is.</summary>
    public async Task<DateTimeOffset?> AsOfAsync(Guid customerId, Guid orderId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return await connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(
            new CommandDefinition(
                AsOfSql,
                new { OrderId = orderId, CustomerId = customerId },
                cancellationToken: ct));
    }

    private static async Task<ILookup<Guid, OrderLineReadRow>> PageLinesAsync(
        IDbConnection connection,
        List<OrderReadRow> page,
        CancellationToken ct)
    {
        // A first request from a buyer with no orders is ordinary, and its lines can only be none.
        if (page.Count == 0)
            return Array.Empty<OrderLineReadRow>().ToLookup(line => line.OrderId);

        IEnumerable<OrderLineReadRow> lines = await connection.QueryAsync<OrderLineReadRow>(
            new CommandDefinition(
                PageLinesSql,
                new { OrderIds = JsonSerializer.Serialize(page.Select(row => row.OrderId)) },
                cancellationToken: ct));

        return lines.ToLookup(line => line.OrderId);
    }
}
