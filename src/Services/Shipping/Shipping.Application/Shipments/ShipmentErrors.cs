using Common.Application;

namespace Shipping.Application.Shipments;

/// <summary>
/// The catalogue. Every <see cref="Error"/> this service can return is
/// constructed here and nowhere else, which is what keeps <c>Code</c> a bounded
/// set rather than whatever string the nearest handler happened to type.
/// </summary>
/// <remarks>
/// No shipment id and no order id appears in a code below: an id interpolated
/// into a metric dimension is a cardinality incident, and the description is
/// the member written for a person.
/// </remarks>
public static class ShipmentErrors
{
    public static readonly Error NotFound =
        Error.NotFound("shipment.not_found", "No shipment under that identifier.");
}
