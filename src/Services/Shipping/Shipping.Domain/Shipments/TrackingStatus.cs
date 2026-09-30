namespace Shipping.Domain.Shipments;

/// <summary>The platform's closed vocabulary; an unknown carrier word is <see cref="Unrecognised"/>.</summary>
public enum TrackingStatus
{
    Collected,
    InTransit,
    Delivered,
    Unrecognised,
}
