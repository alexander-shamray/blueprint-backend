namespace Shipping.Domain.Shipments;

/// <summary>The widths of a carrier's strings, named once so the aggregate's guards and the columns agree.</summary>
public static class ShipmentLimits
{
    public const int MaxCarrierReferenceLength = 64;
    public const int MaxTrackingNumberLength = 64;
    public const int MaxUnfulfillableReasonLength = 100;
    public const int MaxCarrierEventIdLength = 100;
}
