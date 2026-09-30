namespace Shipping.Domain.Shipments;

/// <summary>One fact the carrier reported, reached only through its <see cref="Shipment"/>.</summary>
/// <remarks>Not an <see cref="Common.Domain.Entity{TId}"/>, which keys on one struct; this keys on two.</remarks>
public sealed class TrackingEvent
{
    public ShipmentId ShipmentId { get; private set; }

    public string CarrierEventId { get; private set; } = "";

    public TrackingStatus Status { get; private set; }

    /// <summary>The carrier's timestamp, bounded by the adapter before it arrives.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    // EF Core materialisation only (§5.4).
    private TrackingEvent() { }

    /// <summary>Internal, so only <see cref="Shipment"/> creates one and its deduplication holds.</summary>
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
