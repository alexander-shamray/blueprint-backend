using Common.Domain;
using Shipping.Domain.Shipments.Events;

namespace Shipping.Domain.Shipments;

/// <summary>
/// §3.2's aggregate: one shipment per confirmed order, and the spec's
/// section 5 table is its states and the only moves between them.
/// </summary>
/// <remarks>
/// Every operation returns whether it moved the shipment; a superseded
/// arrival returns <c>false</c> rather than throwing, because a throw is a
/// row retried for ever in a worker and a redelivery loop in a consumer. The
/// backoff, the lease and the poll schedule are properties and no behaviour.
/// </remarks>
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

    /// <summary>
    /// When the shipment reached a terminal state, and the clock the address
    /// and tracking retention windows are measured from (ADR-053).
    /// </summary>
    public DateTimeOffset? TerminalAt { get; private set; }

    public int Attempts { get; private set; }

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
        NextAttemptAt = now;
    }

    /// <summary>The first row of the spec's section 5 table: a confirmed order makes a pending shipment.</summary>
    public static Shipment For(ShipmentId id, OrderId orderId, DateTimeOffset now) => new(id, orderId, now);

    /// <summary>The carrier booked it, and answered with a reference and a tracking number.</summary>
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

    /// <summary>
    /// <c>OrderCancelled</c> arrived. A pending shipment is voided at once and
    /// is never booked; a booked one only records the request, because the
    /// parcel may already be moving and the carrier decides (spec, section 6).
    /// </summary>
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

    /// <summary>The carrier cancelled it.</summary>
    public bool CarrierCancelled(DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Booked || CancellationRequestedAt is null)
            return false;

        Status = ShipmentStatus.Voided;
        TerminalAt = now;
        return true;
    }

    /// <summary>
    /// The carrier answered that it has gone. Tracking goes on and the
    /// despatch is published when <c>Collected</c> arrives; the saga's
    /// <c>CancelledAfterConfirmation</c> review row is the one record of the
    /// disagreement (spec, section 6).
    /// </summary>
    public bool CarrierRefusedCancellation(DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Booked || CancellationRequestedAt is null || CancellationRefusedAt is not null)
            return false;

        CancellationRefusedAt = now;
        return true;
    }

    /// <summary>
    /// One arrival from the carrier's feed. The row is kept whether or not it
    /// moves the shipment — a carrier's fact is a fact — and the return says
    /// only whether the state moved.
    /// </summary>
    public bool Record(string carrierEventId, TrackingStatus status, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        Require(carrierEventId, ShipmentLimits.MaxCarrierEventIdLength, "the carrier's event id");

        // The key makes a repeated page free (spec, section 5).
        if (_trackingEvents.Any(e => e.CarrierEventId == carrierEventId))
            return false;

        _trackingEvents.Add(new TrackingEvent(Id, carrierEventId, status, occurredAt, now));

        return status switch
        {
            TrackingStatus.Collected => Dispatch(occurredAt),
            TrackingStatus.Delivered => Deliver(occurredAt, now),
            _ => false,
        };
    }

    private bool Dispatch(DateTimeOffset occurredAt)
    {
        if (Status != ShipmentStatus.Booked)
            return false;

        Status = ShipmentStatus.Dispatched;
        Raise(new ShipmentDispatchedDomainEvent(Id, OrderId, TrackingNumber!, occurredAt));
        return true;
    }

    private bool Deliver(DateTimeOffset occurredAt, DateTimeOffset now)
    {
        if (Status is not (ShipmentStatus.Booked or ShipmentStatus.Dispatched))
            return false;

        // The despatch first when it was never raised. A delivery ahead of a
        // despatch is not a timeline, and the two consumers of the first event
        // have already acted by the time the second is read.
        if (Status == ShipmentStatus.Booked)
            Dispatch(occurredAt);

        Status = ShipmentStatus.Delivered;
        TerminalAt = now;
        Raise(new ShipmentDeliveredDomainEvent(Id, OrderId, TrackingNumber!, occurredAt));
        return true;
    }

    /// <summary>
    /// A value the columns cannot hold is §5.7's broken invariant rather than a
    /// no-op: the adapter bounds what the carrier sends (spec, section 9), so
    /// anything arriving here oversized is a defect above this line.
    /// </summary>
    private static void Require(string value, int maxLength, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"A shipment needs {what}.");

        if (value.Length > maxLength)
            throw new DomainException($"{what} is longer than {maxLength} characters; the adapter bounds it.");
    }
}
