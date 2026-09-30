using Common.Domain;
using Shipping.Domain.Shipments.Events;

namespace Shipping.Domain.Shipments;

/// <summary>§3.2's aggregate, whose moves return <c>false</c> on a superseded arrival rather than throw.</summary>
/// <remarks>The backoff, lease and poll columns are the workers' (ADR-054); the aggregate only resets them.</remarks>
public sealed class Shipment : AggregateRoot<ShipmentId>
{
    private readonly List<TrackingEvent> _trackingEvents = [];

    public OrderId OrderId { get; private set; }

    public ShipmentStatus Status { get; private set; }

    public string? CarrierReference { get; private set; }

    public string? TrackingNumber { get; private set; }

    public string? UnfulfillableReason { get; private set; }

    public DateTimeOffset? CancellationRequestedAt { get; private set; }

    public DateTimeOffset? CancellationRefusedAt { get; private set; }

    /// <summary>The clock the address and tracking retention windows are measured from (ADR-053).</summary>
    public DateTimeOffset? TerminalAt { get; private set; }

    /// <summary>The clock ADR-052's give-up age is measured from.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The fulfilment pass's failed passes on this row (ADR-054).</summary>
    public int Attempts { get; private set; }

    /// <summary>The tracking pass's failed passes on this row (ADR-054).</summary>
    public int PollAttempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? NextPollAt { get; private set; }

    public IReadOnlyList<TrackingEvent> TrackingEvents => _trackingEvents.AsReadOnly();

    // EF Core materialisation only (§5.4).
    private Shipment() { }

    private Shipment(ShipmentId id, OrderId orderId, DateTimeOffset now)
    {
        Id = id;
        OrderId = orderId;
        Status = ShipmentStatus.Pending;
        CreatedAt = now;
        NextAttemptAt = now;
    }

    public static Shipment For(ShipmentId id, OrderId orderId, DateTimeOffset now) => new(id, orderId, now);

    public bool Book(string carrierReference, string trackingNumber, DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Pending)
            return false;

        Require(carrierReference, ShipmentLimits.MaxCarrierReferenceLength, "the carrier's reference");
        Require(trackingNumber, ShipmentLimits.MaxTrackingNumberLength, "a tracking number");

        Status = ShipmentStatus.Booked;
        CarrierReference = carrierReference;
        TrackingNumber = trackingNumber;
        NextPollAt = now;
        return true;
    }

    /// <summary>The address owner or the carrier answered that it cannot be done.</summary>
    public bool MarkUnfulfillable(string reason, DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Pending)
            return false;

        Require(reason, ShipmentLimits.MaxUnfulfillableReasonLength, "a reason");

        Status = ShipmentStatus.Unfulfillable;
        UnfulfillableReason = reason;
        TerminalAt = now;
        return true;
    }

    /// <summary>Voids a pending shipment; on a booked one it records the request, and the carrier decides.</summary>
    public bool Cancel(DateTimeOffset now)
    {
        if (Status == ShipmentStatus.Pending)
        {
            Status = ShipmentStatus.Voided;
            TerminalAt = now;
            return true;
        }

        if (Status != ShipmentStatus.Booked || CancellationRequestedAt is not null)
            return false;

        CancellationRequestedAt = now;
        return true;
    }

    public bool CarrierCancelled(DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Booked || CancellationRequestedAt is null)
            return false;

        Status = ShipmentStatus.Voided;
        TerminalAt = now;

        // Unscheduled, so the tracking claim's index holds only rows it may take.
        NextPollAt = null;
        return true;
    }

    /// <summary>The parcel has gone, or the carrier did not answer within the give-up age (ADR-054).</summary>
    public bool CarrierRefusedCancellation(DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Booked || CancellationRequestedAt is null || CancellationRefusedAt is not null)
            return false;

        CancellationRefusedAt = now;
        return true;
    }

    /// <summary>A booked or despatched shipment past ADR-054's tracking age ends here and raises nothing.</summary>
    public bool Abandon(DateTimeOffset now)
    {
        if (Status is not (ShipmentStatus.Booked or ShipmentStatus.Dispatched))
            return false;

        Status = ShipmentStatus.Abandoned;
        TerminalAt = now;
        NextPollAt = null;
        return true;
    }

    /// <summary>Keeps one carrier arrival whether or not it moves the shipment, and says if it did.</summary>
    public bool Record(string carrierEventId, TrackingStatus status, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        Require(carrierEventId, ShipmentLimits.MaxCarrierEventIdLength, "the carrier's event id");

        // The key makes a repeated page free, compared as the table compares it, so nothing is refused at commit.
        if (_trackingEvents.Any(e => SameEventId(e.CarrierEventId, carrierEventId)))
            return false;

        _trackingEvents.Add(new TrackingEvent(Id, carrierEventId, status, occurredAt, now));

        // The events carry the recording instant, not the carrier's: §13.3's lag is measured from the raise.
        return status switch
        {
            TrackingStatus.Collected => Dispatch(now),
            TrackingStatus.Delivered => Deliver(now),
            _ => false,
        };
    }

    /// <summary>Drops the fulfilment pass's lease and resets its backoff, the claim itself being raw SQL.</summary>
    public void ReleaseClaim()
    {
        LockedUntil = null;
        Attempts = 0;
    }

    /// <summary>Drops the tracking pass's lease, resets its backoff, and schedules a live row's poll.</summary>
    /// <remarks><c>Attempts</c> is the fulfilment pass's, and this pass leaves it alone (ADR-054).</remarks>
    public void PollApplied(DateTimeOffset nextPollAt)
    {
        NextPollAt = Status is ShipmentStatus.Booked or ShipmentStatus.Dispatched ? nextPollAt : null;
        LockedUntil = null;
        PollAttempts = 0;
    }

    private bool Dispatch(DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Booked)
            return false;

        Status = ShipmentStatus.Dispatched;
        Raise(new ShipmentDispatchedDomainEvent(Id, OrderId, TrackingNumber!, now));
        return true;
    }

    private bool Deliver(DateTimeOffset now)
    {
        if (Status is not (ShipmentStatus.Booked or ShipmentStatus.Dispatched))
            return false;

        // The despatch first when it was never raised, so the two events keep their order.
        if (Status == ShipmentStatus.Booked)
            Dispatch(now);

        Status = ShipmentStatus.Delivered;
        TerminalAt = now;
        Raise(new ShipmentDeliveredDomainEvent(Id, OrderId, TrackingNumber!, now));
        return true;
    }

    /// <summary>Throws: the adapter bounds what the carrier sends, so an oversized value is a defect.</summary>
    private static void Require(string value, int maxLength, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"A shipment needs {what}.");

        if (value.Length > maxLength)
            throw new DomainException($"{what} is longer than {maxLength} characters; the adapter bounds it.");
    }

    /// <summary>Case-sensitive as the binary collation, and blind to trailing spaces as SQL Server is.</summary>
    private static bool SameEventId(string left, string right) =>
        string.Equals(left.TrimEnd(' '), right.TrimEnd(' '), StringComparison.Ordinal);
}
