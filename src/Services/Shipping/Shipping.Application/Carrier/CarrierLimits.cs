using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>The shipment's widths, enforced by the adapter before a row is written, not at the insert.</summary>
public static class CarrierLimits
{
    public const int MaxReferenceLength = ShipmentLimits.MaxCarrierReferenceLength;

    public const int MaxTrackingNumberLength = ShipmentLimits.MaxTrackingNumberLength;

    public const int MaxReasonLength = ShipmentLimits.MaxUnfulfillableReasonLength;

    public const int MaxCarrierEventIdLength = ShipmentLimits.MaxCarrierEventIdLength;
}
