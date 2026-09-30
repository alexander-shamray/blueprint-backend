using Common.Application;

namespace Shipping.Application.Shipments;

/// <summary>Every <see cref="Error"/> is constructed here, so <c>Code</c> stays closed (§10.5).</summary>
public static class ShipmentErrors
{
    public static readonly Error NotFound =
        Error.NotFound("shipment.not_found", "No shipment under that identifier.");
}
