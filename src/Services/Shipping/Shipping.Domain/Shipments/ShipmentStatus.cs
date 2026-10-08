namespace Shipping.Domain.Shipments;

/// <summary>Every state but <c>Pending</c>, <c>Booked</c> and <c>Dispatched</c> is terminal (ADR-054).</summary>
/// <remarks>
/// One exit: a <c>Voided</c> row that never held a reference reopens as booked when a booking lands on it
/// (<see cref="Shipment.KeepUnreturnedBooking"/>). Member order is no contract: moves guard on named states,
/// and the column stores names (§7.2).
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
