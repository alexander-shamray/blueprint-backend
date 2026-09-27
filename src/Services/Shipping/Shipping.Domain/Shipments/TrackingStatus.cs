namespace Shipping.Domain.Shipments;

/// <summary>
/// The platform's own tracking vocabulary, closed (spec, section 5). A carrier
/// word the translation does not know is stored as <see cref="Unrecognised"/>
/// and moves nothing: a carrier adds statuses on its own schedule, and a
/// conformist that faulted on a new one would stop tracking every shipment
/// until a deploy.
/// </summary>
public enum TrackingStatus
{
    Collected,
    InTransit,
    Delivered,
    Unrecognised,
}
