namespace Web.Bff.Tests;

/// <summary>One <c>bff.Orders</c> row as the engine holds it, read past the model so a test sees the columns.</summary>
public sealed record ProjectedOrder
{
    public Guid OrderId { get; init; }

    public Guid? CustomerId { get; init; }

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

    /// <summary>The facts alone, for comparing two rows built in two orders.</summary>
    public ProjectedOrder Facts() => this with { OrderId = Guid.Empty, FirstSeenAt = default, AsOf = default };
}

/// <summary>One <c>bff.OrderLines</c> row.</summary>
public sealed record ProjectedLine
{
    public int LineNumber { get; init; }

    public Guid ProductId { get; init; }

    public int Quantity { get; init; }

    public decimal UnitPrice { get; init; }
}
