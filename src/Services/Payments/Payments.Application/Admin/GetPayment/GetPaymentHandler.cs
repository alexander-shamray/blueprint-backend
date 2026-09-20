using System.Data;
using Common.Application;
using Dapper;

namespace Payments.Application.Admin.GetPayment;

/// <summary>
/// §6.5's read side over the three write tables: what Payments holds for one
/// order, for the runbook's first question (spec, section 10).
/// </summary>
/// <remarks>
/// An intent or refund row without its record cannot exist — every writer locks
/// or stamps the record first — so <c>PaymentOrders</c> leads the join and a
/// missing record is this query's only <c>null</c>.
/// </remarks>
public sealed class GetPaymentHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetPaymentQuery, PaymentView?>
{
    private const string Sql =
        """
        SELECT o.OrderId, o.PlacedAt, o.CancelledAt,
               i.Status, i.Reference, i.Amount, i.Currency, i.DeclineReason, i.CreatedAt,
               r.Reference AS RefundReference, r.VoidedAt
        FROM payments.PaymentOrders o
        LEFT JOIN payments.PaymentIntents i ON i.OrderId = o.OrderId
        LEFT JOIN payments.Refunds r ON r.OrderId = o.OrderId
        WHERE o.OrderId = @OrderId;
        """;

    public async Task<PaymentView?> HandleAsync(GetPaymentQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        Row? row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(Sql, new { query.OrderId }, cancellationToken: ct));

        if (row is null)
            return null;

        // Status is the outer join's witness for the intent, and RefundReference
        // for the refund: both columns are NOT NULL in their own table, so a
        // null here is the absent row rather than an absent value.
        IntentView? intent = row.Status is null
            ? null
            : new IntentView(
                row.Status,
                row.Reference,
                row.Amount!.Value,
                // char(3) pads a shorter code, and the answer carries the
                // currency rather than the column's width.
                row.Currency!.Trim(),
                row.DeclineReason,
                row.CreatedAt!.Value);

        RefundView? refund = row.RefundReference is null
            ? null
            : new RefundView(row.RefundReference, row.VoidedAt!.Value);

        return new PaymentView(row.OrderId, new OrderView(row.PlacedAt, row.CancelledAt), intent, refund);
    }

    /// <summary>
    /// The join's flat shape, every joined column nullable because the two
    /// joins are outer ones.
    /// </summary>
    private sealed record Row(
        Guid OrderId,
        DateTimeOffset? PlacedAt,
        DateTimeOffset? CancelledAt,
        string? Status,
        string? Reference,
        decimal? Amount,
        string? Currency,
        string? DeclineReason,
        DateTimeOffset? CreatedAt,
        string? RefundReference,
        DateTimeOffset? VoidedAt);
}
