namespace Shipping.Domain.Shipments;

/// <summary>
/// One fact the carrier reported about a shipment (spec, section 5). An entity
/// of <see cref="Shipment"/>, reached only through it.
/// </summary>
/// <remarks>
/// Not an <c>Entity&lt;TId&gt;</c>: its identity is
/// <c>(ShipmentId, CarrierEventId)</c> and that base type keys on one struct.
/// The carrier's own id is what makes a repeated page free, and it orders
/// nothing — which is why the state machine is monotonic by rank.
/// </remarks>
public sealed class TrackingEvent
{
    public ShipmentId ShipmentId { get; private set; }

    public string CarrierEventId { get; private set; } = "";

    public TrackingStatus Status { get; private set; }

    /// <summary>The carrier's timestamp, bounded by the adapter before it arrives.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>When this service first saw it, which is the clock retention ages by.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    // EF Core materialisation only (§5.4).
    private TrackingEvent() { }

    /// <summary>
    /// <c>internal</c>, not public: an event is created by
    /// <see cref="Shipment"/> and by nothing else, so the deduplication and
    /// the promotion cannot be bypassed.
    /// </summary>
    internal TrackingEvent(
        ShipmentId shipmentId,
        string carrierEventId,
        TrackingStatus status,
        DateTimeOffset occurredAt,
        DateTimeOffset recordedAt)
    {
        ShipmentId = shipmentId;
        CarrierEventId = carrierEventId;
        Status = status;
        OccurredAt = occurredAt;
        RecordedAt = recordedAt;
    }
}
