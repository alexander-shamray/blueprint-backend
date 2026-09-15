namespace Common.Contracts.Ordering.V1;

/// <summary>
/// An order was cancelled (§3.2). Inventory releases stock, Payments voids an
/// authorisation, and Ordering's own saga stops: §11.4's endpoint cancels the
/// aggregate, and this event is how that reaches the workflow.
/// </summary>
/// <remarks>
/// <see cref="Reason"/> is a <see cref="CancelReasons"/> code rather than
/// Ordering's enum, which would put its domain in every consumer (§9.1), and it
/// says what was asserted rather than who asked, which is <see cref="Origin"/>.
/// </remarks>
public sealed record OrderCancelled : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string Reason { get; init; }

    /// <summary>
    /// Who asked — a <see cref="CancelOrigins"/> code.
    /// </summary>
    /// <remarks>
    /// Optional, so additive under §9.2. Absent means published
    /// before the field existed, and a consumer keeps its earlier behaviour for
    /// good: an error queue or a replay can deliver such a payload at any time,
    /// and making it <c>required</c> would be a V2.
    /// </remarks>
    public string? Origin { get; init; }
}
