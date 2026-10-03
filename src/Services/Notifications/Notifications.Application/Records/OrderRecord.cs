namespace Notifications.Application.Records;

/// <summary>Ordering's word on one order: whose it is, and whether, why and by whom it was cancelled.</summary>
/// <remarks>
/// ADR-017's local projection, which four events naming only an order wait on for their customer; a cancellation is
/// set once and never cleared, so it stays ADR-049's deciding fact.
/// </remarks>
public sealed class OrderRecord
{
    public Guid OrderId { get; private set; }

    /// <summary>Pseudonymous personal data, so the record has a window and an erasure path (§11.7).</summary>
    public Guid CustomerId { get; private set; }

    /// <summary>The cancellation's own instant, or null while no <c>OrderCancelled</c> has arrived.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>A <c>CancelReasons</c> code as published, or null when it failed the intake's check.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>A <c>CancelOrigins</c> code, or null for a publisher that predates it (§9.2).</summary>
    public string? CancelOrigin { get; private set; }

    /// <summary>When this service first heard of the order, the instant <c>OrderRetention</c> ages it from.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    // EF Core materialisation only (§5.4).
    private OrderRecord() { }

    private OrderRecord(Guid orderId, Guid customerId, DateTimeOffset now)
    {
        OrderId = orderId;
        CustomerId = customerId;
        RecordedAt = now;
    }

    /// <summary>The first of Ordering's events for an order names its customer.</summary>
    public static OrderRecord For(Guid orderId, Guid customerId, DateTimeOffset now) => new(orderId, customerId, now);

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
