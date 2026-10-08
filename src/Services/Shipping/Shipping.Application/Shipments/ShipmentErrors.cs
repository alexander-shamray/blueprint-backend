using Common.Application;

namespace Shipping.Application.Shipments;

/// <summary>Every <see cref="Error"/> is constructed here, so <c>Code</c> stays closed (§10.5).</summary>
public static class ShipmentErrors
{
    public static readonly Error NotFound =
        Error.NotFound("shipment.not_found", "No shipment under that identifier.");

    public static readonly Error NotTrackable =
        Error.Rule("shipment.not_trackable", "The shipment is no longer tracked, so there is nothing to abandon.");
}
