namespace Shipping.Domain.Shipments;

/// <summary>
/// The spec's section 5 states and ADR-054's <c>Abandoned</c>; every state
/// but <c>Pending</c>, <c>Booked</c> and <c>Dispatched</c> is terminal.
/// </summary>
/// <remarks>
/// Every arrival may only move the shipment forward: a carrier page orders
/// nothing (spec, section 5), so each operation guards on the states it may
/// move from rather than on a comparison of members. Their order is not a
/// contract either — the column stores the name (§7.2).
/// </remarks>
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
