namespace Shipping.Domain.Shipments;

/// <summary>
/// The widths this service stores a carrier's strings at, named once because
/// the aggregate's guards and the entity configurations must agree: a value
/// the column refuses and the domain accepted is a commit that fails at
/// <c>SaveChanges</c>, one layer away from whatever produced it.
/// </summary>
public static class ShipmentLimits
{
    public const int MaxCarrierReferenceLength = 64;
    public const int MaxTrackingNumberLength = 64;
    public const int MaxUnfulfillableReasonLength = 100;
    public const int MaxCarrierEventIdLength = 100;
}
