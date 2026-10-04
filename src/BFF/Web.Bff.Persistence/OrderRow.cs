namespace Web.Bff.Persistence;

/// <summary>One order as the projection knows it: a column per fact §10.7 returns, each set once (ADR-051).</summary>
/// <remarks>
/// Written and read through SQL, never through this type, which exists to define the table (§7.2). Every column
/// but the key and the two BFF instants is nullable, because any of the seven order events can create it.
/// </remarks>
public sealed class OrderRow
{
    public Guid OrderId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public string? Currency { get; private set; }

    public decimal? TotalAmount { get; private set; }

    public DateTimeOffset? PlacedAt { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? CancelOutcome { get; private set; }

    public DateTimeOffset? AuthorisedAt { get; private set; }

    public decimal? AuthorisedAmount { get; private set; }

    public DateTimeOffset? RefundedAt { get; private set; }

    public decimal? RefundedAmount { get; private set; }

    /// <summary>The payment events' currency, labelling both amounts; one may precede <c>Currency</c>.</summary>
    public string? PaymentCurrency { get; private set; }

    public string? TrackingNumber { get; private set; }

    /// <summary>The BFF's clock at insert, which orders the list and never changes.</summary>
    public DateTimeOffset FirstSeenAt { get; private set; }

    /// <summary>The BFF's clock at the row's last write, which both routes return.</summary>
    public DateTimeOffset AsOf { get; private set; }
}
