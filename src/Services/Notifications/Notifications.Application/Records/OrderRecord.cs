namespace Notifications.Application.Records;

/// <summary>Ordering's word on one order: whose it is, and whether, why and by whom it was cancelled.</summary>
/// <remarks>
/// ADR-017's local projection, which four events naming only an order wait on for their customer; a cancellation is
/// set once and never cleared, so it stays ADR-049's deciding fact.
/// </remarks>
public sealed class OrderRecord
{
    public Guid OrderId { get; private set; }

    /// <summary>Pseudonymous personal data, so the record owes a window and an erasure path (§11.7).</summary>
    public Guid CustomerId { get; private set; }

    /// <summary>The cancellation's own instant, or null while no <c>OrderCancelled</c> has arrived.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>A <c>CancelReasons</c> code as published, or null when it failed the intake's check.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>A <c>CancelOrigins</c> code as published, or null when absent (§9.2) or failing the check.</summary>
    public string? CancelOrigin { get; private set; }

    /// <summary>When this service first heard of the order, the instant <c>OrderRetention</c> ages it from.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>
    /// The erasure requests the customer was already under when the order was placed, as the first event named them
    /// (ADR-099); empty from a publisher that does not say, so the worker's question is then unbounded (ADR-101).
    /// </summary>
    public IReadOnlyList<Guid> ErasedUnder { get; private set; } = [];

    // EF Core materialisation only (§5.4).
    private OrderRecord() { }

    private OrderRecord(Guid orderId, Guid customerId, IReadOnlyList<Guid> erasedUnder, DateTimeOffset now)
    {
        OrderId = orderId;
        CustomerId = customerId;
        ErasedUnder = erasedUnder;
        RecordedAt = now;
    }

    /// <summary>The first of Ordering's events for an order names its customer and the requests it was placed after.</summary>
    public static OrderRecord For(
        Guid orderId,
        Guid customerId,
        DateTimeOffset now,
        IReadOnlyList<Guid>? erasedUnder = null) =>
        new(orderId, customerId, [.. (erasedUnder ?? []).Distinct()], now);

    /// <summary>Records the cancellation once; a second one keeps the first and returns false.</summary>
    public bool Cancel(string? reason, string? origin, DateTimeOffset at)
    {
        Bound(reason, nameof(reason));
        Bound(origin, nameof(origin));

        if (CancelledAt is not null)
            return false;

        CancelledAt = at;
        CancelReason = reason;
        CancelOrigin = origin;
        return true;
    }

    // A value the column cannot hold is the caller's defect, since the intake checks every code before this.
    private static void Bound(string? value, string name)
    {
        if (value is not null)
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, OrderRecordLimits.MaxCodeLength, name);
    }
}
