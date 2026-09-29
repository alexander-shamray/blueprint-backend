namespace Shipping.Infrastructure.Tracking;

/// <summary>
/// One row a tracking pass has leased, projected to exactly what the poll
/// needs — <c>FulfilmentWork</c>'s counterpart, with the carrier's reference
/// non-null because only a booked shipment has one, and the shipment's making,
/// which ADR-054's tracking age is measured from.
/// </summary>
/// <remarks>
/// No address is projected here, which is what keeps this worker's log lines
/// free of one (spec, section 11).
/// </remarks>
public sealed record TrackingWork(
    Guid Id,
    Guid OrderId,
    string CarrierReference,
    int PollAttempts,
    DateTimeOffset CreatedAt);
