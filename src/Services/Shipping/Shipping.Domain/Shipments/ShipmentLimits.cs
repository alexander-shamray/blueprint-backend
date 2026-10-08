namespace Shipping.Domain.Shipments;

/// <summary>The widths of a carrier's strings and the count of its events, named once for guards and columns.</summary>
public static class ShipmentLimits
{
    public const int MaxCarrierReferenceLength = 64;
    public const int MaxTrackingNumberLength = 64;
    public const int MaxUnfulfillableReasonLength = 100;
    public const int MaxCarrierEventIdLength = 100;

    /// <summary>Events one shipment keeps: far past a parcel's scans, so a feed of fresh ids cannot grow it.</summary>
    public const int MaxTrackingEvents = 500;
}
