namespace Web.Bff.Orders;

/// <summary>§10.7's wire shape from the projection's rows: every rule the response states, and no query.</summary>
public static class OrderView
{
    public static OrderSummary Summary(OrderReadRow row, IReadOnlyList<OrderLineReadRow> lines)
    {
        string status = StatusOf(row);

        return new OrderSummary(
            row.OrderId,
            status,
            TimelineOf(row),
            row.RefundedAt is not null,
            row.RefundedAt,
            IsCancellable(status),
            TotalOf(row),
            [.. Priced(row, lines).Select(l => new OrderLineSummary(l.ProductId, l.ProductName, LineTotal(l, row)))],
            row.AsOf);
    }

    public static OrderDetail Detail(OrderReadRow row, IReadOnlyList<OrderLineReadRow> lines)
    {
        string status = StatusOf(row);

        return new OrderDetail(
            row.OrderId,
            status,
            TimelineOf(row),
            row.RefundedAt is not null,
            row.RefundedAt,
            IsCancellable(status),
            TotalOf(row),
            [
                .. Priced(row, lines).Select(l => new OrderLineDetail(
                    l.ProductId,
                    l.ProductName,
                    LineTotal(l, row),
                    l.Quantity,
                    new Money(l.UnitPrice, row.Currency!)))
            ],
            row.AsOf,
            PaymentOf(row),
            ShipmentOf(row));
    }

    /// <summary><c>Order.Cancel</c>'s rule from a projection that lags it: a hint, not an authority (§10.7).</summary>
    public static bool IsCancellable(string status) =>
        status is BuyerStatuses.Placed or BuyerStatuses.Confirmed;

    private static string StatusOf(OrderReadRow row) =>
        BuyerStatus.Of(new OrderSteps(
                row.PlacedAt,
                row.ConfirmedAt,
                row.DispatchedAt,
                row.DeliveredAt,
                row.CancelledAt,
                row.CancelOutcome)) ??
            throw new InvalidOperationException(
                $"Order {row.OrderId} is owned and has no step. Only the three Ordering events set a " +
                "customer, and each sets its own step in the same statement (ADR-051), so this row " +
                "was written by something other than the projection's handlers.");

    private static OrderTimeline TimelineOf(OrderReadRow row) =>
        new(row.PlacedAt, row.ConfirmedAt, row.DispatchedAt, row.DeliveredAt, row.CancelledAt);

    private static Money? TotalOf(OrderReadRow row) =>
        row.Currency is null ? null : new Money(row.TotalAmount!.Value, row.Currency);

    // Lines and the currency commit together, but the read takes two snapshots, so lines met first wait for it.
    private static IEnumerable<OrderLineReadRow> Priced(OrderReadRow row, IReadOnlyList<OrderLineReadRow> lines) =>
        row.Currency is null ? [] : lines.OrderBy(l => l.LineNumber);

    private static Money LineTotal(OrderLineReadRow line, OrderReadRow row) =>
        new(line.UnitPrice * line.Quantity, row.Currency!);

    private static PaymentFacts? PaymentOf(OrderReadRow row) =>
        row.PaymentCurrency is null
            ? null
            : new PaymentFacts(
                row.AuthorisedAt,
                row.AuthorisedAmount is { } authorised ? new Money(authorised, row.PaymentCurrency) : null,
                row.RefundedAmount is { } refunded ? new Money(refunded, row.PaymentCurrency) : null);

    private static ShipmentFacts? ShipmentOf(OrderReadRow row) =>
        (row.DispatchedAt ?? row.DeliveredAt) is null
            ? null
            : new ShipmentFacts(row.TrackingNumber, row.DispatchedAt, row.DeliveredAt);
}
