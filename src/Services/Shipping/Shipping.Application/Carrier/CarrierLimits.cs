using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>
/// What a carrier's answer may carry and still be recorded.
/// <c>Shipment.CarrierReference</c>, <c>Shipment.TrackingNumber</c> and
/// <c>Shipment.UnfulfillableReason</c> are these widths, and
/// <c>TrackingEvent</c>'s key holds the event id, so the adapter refuses a
/// longer answer before a row is written rather than at the insert: a
/// booking that cannot be recorded is a parcel the carrier is holding with
/// nothing on the row to cancel it by.
/// </summary>
public static class CarrierLimits
{
    public const int MaxReferenceLength = ShipmentLimits.MaxCarrierReferenceLength;

    public const int MaxTrackingNumberLength = ShipmentLimits.MaxTrackingNumberLength;

    public const int MaxReasonLength = ShipmentLimits.MaxUnfulfillableReasonLength;

    public const int MaxCarrierEventIdLength = ShipmentLimits.MaxCarrierEventIdLength;
}
