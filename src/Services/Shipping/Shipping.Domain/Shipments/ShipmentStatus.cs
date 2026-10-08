namespace Shipping.Domain.Shipments;

/// <summary>All but <c>Pending</c>, <c>Booked</c> and <c>Dispatched</c> are terminal, bar one exit (ADR-054).</summary>
/// <remarks>
/// A <c>Voided</c> row with no reference reopens when a booking the carrier would not take back lands on it
/// (<see cref="Shipment.KeepUnreturnedBooking"/>). Member order is no contract: the column stores names (§7.2).
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
