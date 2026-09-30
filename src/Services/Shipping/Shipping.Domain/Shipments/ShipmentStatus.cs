namespace Shipping.Domain.Shipments;

/// <summary>Every state but <c>Pending</c>, <c>Booked</c> and <c>Dispatched</c> is terminal (ADR-054).</summary>
/// <remarks>Member order is no contract: moves guard on named states, and the column stores names (§7.2).</remarks>
public enum ShipmentStatus
{
    Pending,
    Booked,
    Dispatched,
    Delivered,
    Voided,
    Unfulfillable,
    Abandoned,
}
