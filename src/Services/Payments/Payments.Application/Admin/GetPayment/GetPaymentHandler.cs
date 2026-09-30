using System.Data;
using Common.Application;
using Dapper;

namespace Payments.Application.Admin.GetPayment;

/// <summary>§6.5's read side over the three write tables: what Payments holds for one order.</summary>
/// <remarks>
/// Every writer locks or stamps the record first, through <see cref="Orders.IPaymentOrderStore"/>, so
/// <c>PaymentOrders</c> leads the join and a missing record is this query's only <c>null</c>.
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

        // Status and RefundReference are NOT NULL in their tables, so a null here is an absent row.
        IntentView? intent = row.Status is null
            ? null
            : new IntentView(
                row.Status,
                row.Reference,
                row.Amount!.Value,
                // char(3) pads a shorter code.
                row.Currency!.Trim(),
                row.DeclineReason,
                row.CreatedAt!.Value);

        RefundView? refund = row.RefundReference is null
            ? null
            : new RefundView(row.RefundReference, row.VoidedAt!.Value);

        return new PaymentView(row.OrderId, new OrderView(row.PlacedAt, row.CancelledAt), intent, refund);
    }

    /// <summary>The join's flat shape, nullable because both joins are outer.</summary>
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
