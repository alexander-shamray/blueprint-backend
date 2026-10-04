namespace Web.Bff.Orders;

/// <summary>One <c>bff.Orders</c> row as the read selects it; the customer is the query's, never read back.</summary>
public sealed record OrderReadRow
{
    public Guid OrderId { get; init; }

    public string? Currency { get; init; }

    public decimal? TotalAmount { get; init; }

    public DateTimeOffset? PlacedAt { get; init; }

    public DateTimeOffset? ConfirmedAt { get; init; }

    public DateTimeOffset? DispatchedAt { get; init; }

    public DateTimeOffset? DeliveredAt { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public string? CancelOutcome { get; init; }

    public DateTimeOffset? AuthorisedAt { get; init; }

    public decimal? AuthorisedAmount { get; init; }

    public DateTimeOffset? RefundedAt { get; init; }

    public decimal? RefundedAmount { get; init; }

    public string? PaymentCurrency { get; init; }

    public string? TrackingNumber { get; init; }

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset AsOf { get; init; }
}

/// <summary>One <c>bff.OrderLines</c> row with the name <c>bff.Products</c> holds for it, if any.</summary>
public sealed record OrderLineReadRow
{
    public Guid OrderId { get; init; }

    public int LineNumber { get; init; }

    public Guid ProductId { get; init; }

    public int Quantity { get; init; }

    public decimal UnitPrice { get; init; }

    public string? ProductName { get; init; }
}
